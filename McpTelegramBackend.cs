using System.ComponentModel.DataAnnotations;
using System.Text.Json.Nodes;
using Newtonsoft.Json.Linq;
using TdLib;

namespace TgCli;

internal sealed class McpTelegramBackend : IMcpTelegramBackend
{
    private readonly string? _sessionDirectory;
    private readonly McpSessionGate<TelegramSession> _sessions;

    internal McpTelegramBackend(string? sessionDirectory, int lockTimeout, bool noWait)
        : this(ct => TelegramSession.CreateAsync(sessionDirectory, apiId: 0, apiHash: null, saveConfig: false,
            lockTimeout, noWait, ct), sessionDirectory) { }

    internal McpTelegramBackend(Func<CancellationToken, Task<TelegramSession>> openSession, string? sessionDirectory = null)
    {
        _sessionDirectory = sessionDirectory;
        _sessions = new(openSession);
    }

    public async Task<JsonObject> ExecuteAsync(string name, McpArguments arguments, CancellationToken cancellationToken)
    {
        arguments.Preflight(name);
        var fingerprint = name is "chat_messages" or "message_search" or "forum_topics" ? arguments.Fingerprint(name) : null;
        var cursor = fingerprint is null ? null : McpCursor.Decode(arguments.Text("cursor"), fingerprint);
        if (cursor is not null) cursor.Validate(name);

        var timeout = TimeSpan.FromSeconds(arguments.Int("request_timeout", 60));
        if (name == "diagnostics") return await DiagnoseAsync(timeout, cancellationToken);

        return await _sessions.ExecuteAsync(async (tg, ct) =>
            Wire.ToJsonObject(await ExecuteCoreAsync(tg, name, arguments, fingerprint, cursor, ct)),
            timeout, cancellationToken);
    }

    private async Task<JsonObject> DiagnoseAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        SessionDiagnosticResult? diagnostic = null;
        try
        {
            return await _sessions.ExecuteAsync(async (session, ct) =>
            {
                diagnostic = await SessionDiagnostic.InspectAsync(session, ct);
                // Release the database lease so an external login can repair authorization.
                if (diagnostic.Error is TelegramAuthorizationException authorization) throw authorization;
                return Wire.ToJsonObject(diagnostic.Data);
            }, timeout, cancellationToken);
        }
        catch (Exception error) when (error is ValidationException or IOException or UnauthorizedAccessException)
        {
            return Wire.ToJsonObject((diagnostic ?? SessionDiagnostic.Failure(_sessionDirectory, error)).Data);
        }
    }

    private static async Task<JObject> ExecuteCoreAsync(TelegramSession tg, string name, McpArguments a,
        string? fingerprint, McpCursor? cursor, CancellationToken ct)
    {
        var local = name == "chat_search" ? !a.Bool("server", true) : a.Bool("local");
        if (local) await tg.EnsureReadyAsync(ct);
        else await tg.EnsureConnectedAsync(ct);

        ct.ThrowIfCancellationRequested();
        return name switch
        {
            "chat_list" or "chat_search" => await ChatPageAsync(tg, name, a, local, ct),
            "chat_resolve" => await ChatAsync(tg, await TelegramOperations.ResolveChatAsync(tg, a.Text("username")!, ct), false, ct),
            "chat_messages" or "message_search" => await MessagePageAsync(tg, name, a, local, fingerprint!, cursor, ct),
            "message_get" => await MessageAsync(tg, await tg.Client.GetMessageAsync(a.Id("chat_id"), a.Id("message_id")), false, null, ct),
            "message_link" => Wire.Link(await MessageLinks.BuildAsync(tg, a.Id("chat_id"), a.Id("message_id"), ct, retainNativeOperation: true)),
            "forum_topics" => await TopicsAsync(tg, a, fingerprint!, cursor, ct),
            "chat_context" => await ContextAsync(tg, a, ct),
            "chat_stats" => await StatsAsync(tg, a, local, ct),
            "attachment_download" => await DownloadAsync(tg, a, ct),
            _ => throw new ArgumentException("Unknown Telegram operation.")
        };
    }

    private static async Task<JObject> ChatPageAsync(TelegramSession tg, string name, McpArguments a, bool local, CancellationToken ct)
    {
        var limit = a.Int("limit", 30);
        var query = a.Text("query");
        var list = name == "chat_list" ? await TelegramOperations.ListChatDetailsAsync(tg, limit, ct) : null;
        var chats = list?.Chats ?? await TelegramOperations.SearchChatsAsync(tg, query!, limit, !local, ct);
        var rows = new JArray();
        foreach (var id in chats.ChatIds ?? [])
        {
            ct.ThrowIfCancellationRequested();
            var chat = await tg.Client.GetChatAsync(id);
            rows.Add(await ChatAsync(tg, chat, local, ct));
        }

        var scope = new JObject { ["limit"] = limit };
        if (query is not null)
        {
            scope["query"] = query;
            scope["server"] = !local;
        }
        var reason = list?.TerminationReason ?? "bounded_no_continuation";
        var result = Page(rows, null, list?.Complete ?? false, list?.LimitSatisfied ?? rows.Count >= limit,
            reason, list?.PagesFetched ?? 1, local, scope);
        MarkPartial(result, reason == "chat_list_not_advanced");
        return result;
    }

    private static async Task<JObject> MessagePageAsync(TelegramSession tg, string name, McpArguments a,
        bool local, string fingerprint, McpCursor? cursor, CancellationToken ct)
    {
        var scope = a.CursorScope(name);
        var chatId = a.Id("chat_id");
        var topicId = a.TopicId();
        var start = cursor?.Message ?? 0;
        var limit = a.Int("limit", 30);
        var maxPages = a.Int("max_pages", 100);
        var matches = ChatCommands.MessagePredicate(a.Bool("service_only"), a.Kind());
        string? query = null;
        MessageSearchResult page;
        if (name == "message_search")
        {
            query = a.Text("query", "")!;
            var kind = AttachmentKinds.Parse(a.Text("type", "all")!);
            var filter = AttachmentKinds.ToSearchFilter(AttachmentKinds.CanonicalName(kind));
            var topic = topicId == 0 ? null! : new TdApi.MessageTopic.MessageTopicForum { ForumTopicId = topicId };
            var (since, until) = a.Dates();
            page = await TelegramOperations.SearchMessagePagesAsync(start, 0, limit, all: false, maxPages,
                (anchor, offset, count) => tg.Client.SearchChatMessagesAsync(chatId, topic, query, null!, anchor, offset, count, filter),
                message =>
                {
                    if (!matches(message) || (kind == AttachmentKind.File && MessageFiles.GetAllFiles(message).Count == 0))
                        return false;
                    var date = DateTimeOffset.FromUnixTimeSeconds(message.Date);
                    return (since is null || date >= since) && (until is null || date <= until);
                }, ct);
        }
        else if (topicId != 0)
        {
            var history = await ForumTopicHistory.FetchAsync(tg, chatId, topicId, start, 0, limit, all: false, maxPages,
                a.Int("request_timeout", 60), ct, retainNativeOperation: true, match: matches);
            page = new(history.Messages, history.NextMessageId, history.Complete, history.TerminationReason, history.PagesFetched, history.LimitSatisfied);
        }
        else
        {
            var history = await ChatHistory.FetchAsync(tg, chatId, all: false, local, maxPages, followMigrations: false,
                ct, start, limit: limit, match: matches, requestTimeout: a.Int("request_timeout", 60), retainNativeOperation: true);
            page = new(history.Messages.Select(x => x.Message).ToList(), history.NextMessageId, history.Complete,
                history.TerminationReason, history.PagesFetched, history.LimitSatisfied);
        }

        var rows = await MessagesAsync(tg, page.Messages, local, query, ct);
        var next = page.NextMessageId is long nextId ? new McpCursor(fingerprint, nextId).Encode() : null;
        scope["limit"] = limit;
        scope["max_pages"] = maxPages;
        scope["start_message_id"] = Wire.NullableId(cursor?.Message);
        var result = Page(rows, next, page.Complete, page.LimitSatisfied, page.TerminationReason, page.PagesFetched, local, scope);
        result["next_message_id"] = Wire.NullableId(page.NextMessageId);
        MarkPartial(result, !page.Complete && !page.LimitSatisfied && page.TerminationReason != "local_cache_exhausted");
        return result;
    }

    private static async Task<JObject> TopicsAsync(TelegramSession tg, McpArguments a,
        string fingerprint, McpCursor? cursor, CancellationToken ct)
    {
        var limit = a.Int("limit", 30);
        var maxPages = a.Int("max_pages", 100);
        var start = cursor is null ? default : (cursor.Date, cursor.Message, cursor.Topic);
        var topics = await ForumCommands.FetchTopicsAsync(a.Id("chat_id"), a.Text("query", "")!, limit, all: false,
            maxPages, request => tg.Client.ExecuteAsync(request), ct, start);
        var next = topics.Next is { } offset ? new McpCursor(fingerprint, offset.Message, offset.Date, offset.Topic).Encode() : null;
        var scope = a.CursorScope("forum_topics");
        scope["limit"] = limit;
        scope["max_pages"] = maxPages;
        scope["start_offset"] = cursor is null ? null : TopicOffset(start);
        var result = Page(new JArray(topics.Topics.Select(Wire.Topic)), next, topics.Complete, topics.LimitSatisfied,
            topics.TerminationReason, topics.PagesFetched, false, scope);
        result["next_offset"] = topics.Next is { } nextOffset ? TopicOffset(nextOffset) : null;
        MarkPartial(result, !topics.Complete && !topics.LimitSatisfied);
        return result;
    }

    private static JObject TopicOffset((int Date, long Message, int Topic) offset) => new()
    {
        ["date"] = offset.Date,
        ["message_id"] = Wire.Id(offset.Message),
        ["topic_id"] = Wire.Id(offset.Topic)
    };

    private static async Task<JObject> ContextAsync(TelegramSession tg, McpArguments a, CancellationToken ct)
    {
        var chatId = a.Id("chat_id");
        var messageId = a.Id("message_id");
        var before = a.Int("before", 5);
        var after = a.Int("after", 5);
        var followReplies = a.Bool("follow_reply_chain", true);
        var context = await TelegramOperations.ContextDetailsAsync(tg, chatId, messageId, before, after, followReplies, ct,
            retainNativeOperation: true);
        var scope = new JObject
        {
            ["chat_id"] = Wire.Id(chatId), ["message_id"] = Wire.Id(messageId),
            ["before"] = before, ["after"] = after, ["follow_reply_chain"] = followReplies
        };
        var fulfilled = context.WindowComplete && context.ReplyChainStatus is "complete" or "disabled";
        var result = Page(await MessagesAsync(tg, context.Messages, false, null, ct), null, false, fulfilled,
            fulfilled ? "bounded_context" : "partial_context", null, false, scope);
        result["context"] = Wire.ContextCoverage(context);
        MarkPartial(result, !fulfilled);
        return result;
    }

    private static async Task<JObject> StatsAsync(TelegramSession tg, McpArguments a, bool local, CancellationToken ct)
    {
        var type = AttachmentKinds.CanonicalName(AttachmentKinds.Parse(a.Text("type", "all")!));
        var maxPages = a.Int("max_pages", 100);
        var stats = Wire.Stats(await TelegramOperations.StatsAsync(tg, a.Id("chat_id"), a.TopicId(), type, maxPages,
            local, a.Int("request_timeout", 60), ct, retainNativeOperation: true));
        stats["requested_scope"] = new JObject
        {
            ["chat_id"] = Wire.Id(a.Id("chat_id")), ["topic_id"] = Wire.NullableId(a.TopicId() == 0 ? null : a.TopicId()),
            ["type"] = type, ["max_pages"] = maxPages, ["local"] = local
        };
        MarkPartial(stats, !stats.Value<bool>("complete") && stats.Value<string>("termination_reason") != "local_cache_exhausted");
        return stats;
    }

    private static async Task<JObject> DownloadAsync(TelegramSession tg, McpArguments a, CancellationToken ct)
    {
        var chatId = a.Id("chat_id");
        var messageId = a.Id("message_id");
        var type = a.Text("type");
        var file = await TgCommands.ResolveDownloadFileAsync(tg, type, a.FileId(), chatId, messageId, ct);
        file = await TgCommands.DownloadFileWithRefreshAsync(tg, file, type, chatId, messageId, ct);
        var destination = TgCommands.ResolveDownloadDestination(a.Text("output")!, file);
        await TgCommands.CopyDownloadedFileAsync(file, destination, ct);
        return new() { ["path"] = destination, ["file_id"] = Wire.Id(file.Id), ["size_bytes"] = new FileInfo(destination).Length, ["status"] = "downloaded" };
    }

    private static void MarkPartial(JObject result, bool partial)
    {
        if (partial) result["error_code"] = "partial_result";
    }

    private static async Task<JObject> ChatAsync(TelegramSession tg, TdApi.Chat chat, bool local, CancellationToken ct) =>
        Output.ToChatJson(await Output.BuildChatRowAsync(tg, chat, local, ct));

    private static Task<JObject> MessageAsync(TelegramSession tg, TdApi.Message message, bool local, string? query, CancellationToken ct) =>
        ExportSchema.BuildMessageAsync(tg, new ExportedMessage(message, message.ChatId, message.ChatId, message.Id),
            includeLinks: true, cancellationToken: ct, local: local, query: query);

    private static async Task<JArray> MessagesAsync(TelegramSession tg, IEnumerable<TdApi.Message> messages, bool local,
        string? query, CancellationToken ct)
    {
        var rows = new JArray();
        foreach (var message in messages.OrderBy(m => m.Date).ThenBy(m => m.ChatId).ThenBy(m => m.Id))
        {
            ct.ThrowIfCancellationRequested();
            rows.Add(await MessageAsync(tg, message, local, query, ct));
        }
        return rows;
    }

    private static JObject Page(JArray rows, string? cursor, bool complete, bool limitSatisfied, string reason,
        int? pages, bool local, JObject scope)
    {
        var result = Wire.Coverage(local ? Wire.LocalCache : Wire.Telegram, complete, reason, scope, cursor, pages, rows.Count);
        result["items"] = rows;
        result["next_cursor"] = cursor;
        result["has_more"] = cursor is not null;
        result["limit_satisfied"] = limitSatisfied;
        result["freshness"] = local ? Wire.LocalCache : "tdlib_update_state";
        return result;
    }

    public ValueTask DisposeAsync() => _sessions.DisposeAsync();
}
