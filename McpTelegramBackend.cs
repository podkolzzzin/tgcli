using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Newtonsoft.Json.Linq;
using TdLib;

namespace TgCli;

internal sealed class McpTelegramBackend(string? sessionDirectory, int lockTimeout, bool noWait) : IMcpTelegramBackend
{
    private readonly McpSessionGate<TelegramSession> _sessions = new(
        () => TelegramSession.CreateReadyAsync(sessionDirectory, lockTimeout, noWait));

    public Task<JsonObject> ExecuteAsync(string name, McpArguments arguments, CancellationToken cancellationToken) =>
        _sessions.ExecuteAsync((tg, ct) => ExecuteCoreAsync(tg, name, arguments, ct),
            TimeSpan.FromSeconds(arguments.Int("request_timeout", 60)), cancellationToken);

    private static async Task<JsonObject> ExecuteCoreAsync(TelegramSession tg, string name, McpArguments a, CancellationToken ct)
    {
        var limit = a.Int("limit", 30);
        var cursor = McpCursor.Decode(a.Text("cursor"), a.Fingerprint(name));
        JsonObject result;
        switch (name)
        {
            case "chat_list":
            case "chat_search":
                {
                    if (cursor is not null) throw new ArgumentException("This Telegram operation has no continuation cursor.");
                    var chats = name == "chat_list"
                        ? await TelegramOperations.ListChatsAsync(tg, limit)
                        : await TelegramOperations.SearchChatsAsync(tg, a.Text("query")!, limit, a.Bool("server"));
                    ct.ThrowIfCancellationRequested();
                    var rows = new JsonArray();
                    foreach (var id in chats.ChatIds)
                    {
                        rows.Add(await ChatAsync(tg, await tg.Client.GetChatAsync(id)));
                        ct.ThrowIfCancellationRequested();
                    }
                    result = Page(rows, null, false, false, "bounded_no_continuation");
                    result["limit"] = limit;
                    break;
                }
            case "chat_resolve":
                {
                    result = await ChatAsync(tg, await TelegramOperations.ResolveChatAsync(tg, a.Text("username")!));
                    break;
                }
            case "chat_messages":
            case "message_search":
                {
                    var chat = a.Id("chat_id");
                    var topic = a.Int("topic_id");
                    if (topic > 0 && a.Bool("local")) throw new ArgumentException("local cannot be used with topic_id.");
                    TdApi.Message[] raw;
                    long? searchNext = null;
                    if (name == "message_search")
                    {
                        var found = await tg.Client.SearchChatMessagesAsync(chat,
                            topic > 0 ? new TdApi.MessageTopic.MessageTopicForum { ForumTopicId = topic } : null!,
                            a.Text("query", "")!, null!, cursor?.Message ?? 0, 0, limit,
                            AttachmentKinds.ToSearchFilter(a.Text("type", "all")!));
                        raw = found.Messages;
                        searchNext = found.NextFromMessageId;
                    }
                    else if (topic > 0)
                        raw = (await tg.Client.ExecuteAsync(new TdApi.GetForumTopicHistory
                        { ChatId = chat, ForumTopicId = topic, FromMessageId = cursor?.Message ?? 0, Limit = limit })).Messages_;
                    else raw = (await tg.Client.GetChatHistoryAsync(chat, cursor?.Message ?? 0, 0, limit, a.Bool("local"))).Messages_;
                    ct.ThrowIfCancellationRequested();
                    var page = McpMessagePage.From(raw, cursor?.Message, searchNext);
                    var filtered = ChatCommands.FilterMessages(page.Messages, a.Bool("service_only"), a.Text("kind"));
                    var rows = await MessagesAsync(tg, filtered, ct);
                    result = Page(rows, page.HasMore ? new McpCursor(a.Fingerprint(name), page.Next).Encode() : null,
                        page.HasMore, !page.HasMore && !a.Bool("local"), page.HasMore ? "page_limit" : a.Bool("local") ? "local_cache_exhausted" : "history_exhausted");
                    break;
                }
            case "message_get":
                result = await MessageAsync(tg, await tg.Client.GetMessageAsync(a.Id("chat_id"), a.Id("message_id")));
                break;
            case "message_link":
                {
                    var links = await MessageLinks.BuildAsync(tg, a.Id("chat_id"), a.Id("message_id"));
                    result = new() { ["chat_id"] = Id(links.ChatId), ["message_id"] = Id(links.MessageId), ["short_message_id"] = Id(links.ShortMessageId), ["tg_url"] = links.TgUrl, ["https_url"] = links.HttpsUrl, ["https_fallback"] = links.HttpsFallback };
                    break;
                }
            case "forum_topics":
                {
                    var response = await tg.Client.ExecuteAsync(new TdApi.GetForumTopics
                    {
                        ChatId = a.Id("chat_id"),
                        Query = a.Text("query", "")!,
                        Limit = limit,
                        OffsetDate = cursor?.Date ?? 0,
                        OffsetMessageId = cursor?.Message ?? 0,
                        OffsetForumTopicId = cursor?.Topic ?? 0
                    });
                    ct.ThrowIfCancellationRequested();
                    var next = new McpCursor(a.Fingerprint(name), response.NextOffsetMessageId, response.NextOffsetDate, response.NextOffsetForumTopicId);
                    var more = response.Topics.Length > 0 && (next.Message != 0 || next.Date != 0 || next.Topic != 0);
                    if (more && next == cursor) throw new InvalidOperationException("Forum topic pagination cursor did not advance.");
                    result = Page(new JsonArray(response.Topics.Where(x => x.Info is not null).Select(x => (JsonNode?)new JsonObject
                    {
                        ["chat_id"] = Id(x.Info.ChatId),
                        ["topic_id"] = x.Info.ForumTopicId,
                        ["name"] = x.Info.Name,
                        ["is_general"] = x.Info.IsGeneral,
                        ["is_closed"] = x.Info.IsClosed,
                        ["is_hidden"] = x.Info.IsHidden,
                        ["is_pinned"] = x.IsPinned,
                        ["unread_count"] = x.UnreadCount,
                        ["last_message_id"] = Id(x.LastMessage?.Id ?? 0)
                    }).ToArray()), more ? next.Encode() : null, more, !more, more ? "page_limit" : "topics_exhausted");
                    break;
                }
            case "chat_context":
                {
                    var messages = await TelegramOperations.ContextAsync(tg, a.Id("chat_id"), a.Id("message_id"), a.Int("before", 5), a.Int("after", 5), a.Bool("follow_reply_chain", true), ct);
                    result = Page(await MessagesAsync(tg, messages, ct), null, false, false, "bounded_context");
                    break;
                }
            case "chat_stats":
                result = ConvertObject(await TelegramOperations.StatsAsync(tg, a.Id("chat_id"), a.Int("topic_id"), a.Text("type", "all")!, a.Int("max_pages", 100), a.Bool("local"), a.Int("request_timeout", 60), ct, retainNativeOperation: true));
                break;
            case "attachment_download":
                {
                    var output = a.Text("output")!;
                    if (!Path.IsPathFullyQualified(output)) throw new ArgumentException("output must be an absolute file or directory path.");
                    var file = await TgCommands.ResolveDownloadFileAsync(tg, a.Text("type"), null, a.Id("chat_id"), a.Id("message_id"));
                    ct.ThrowIfCancellationRequested();
                    file = await TgCommands.DownloadFileWithRefreshAsync(tg, file, a.Text("type"), a.Id("chat_id"), a.Id("message_id"));
                    ct.ThrowIfCancellationRequested();
                    if (string.IsNullOrWhiteSpace(file.Local?.Path) || !File.Exists(file.Local.Path)) throw new IOException("TDLib did not provide a downloaded file.");
                    var destination = TgCommands.ResolveDownloadDestination(output, file);
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    if (!Path.GetFullPath(file.Local.Path).Equals(destination, StringComparison.Ordinal)) File.Copy(file.Local.Path, destination, true);
                    result = new() { ["path"] = destination, ["file_id"] = file.Id, ["size_bytes"] = new FileInfo(destination).Length, ["status"] = "downloaded" };
                    break;
                }
            case "diagnostics":
                {
                    var me = await tg.Client.GetMeAsync();
                    result = new() { ["healthy"] = true, ["user_id"] = Id(me.Id), ["display_name"] = $"{me.FirstName} {me.LastName}".Trim(), ["session_directory"] = tg.SessionDirectory, ["checked_at"] = DateTimeOffset.UtcNow.ToString("O") };
                    break;
                }
            default: throw new ArgumentException("Unknown Telegram operation.");
        }
        ct.ThrowIfCancellationRequested();
        return result;
    }

    private static string Id(long value) => value.ToString(CultureInfo.InvariantCulture);
    private static async Task<JsonObject> ChatAsync(TelegramSession tg, TdApi.Chat chat)
    {
        var row = await Output.BuildChatRowAsync(tg, chat);
        return new() { ["chat_id"] = Id(row.ChatId), ["title"] = row.Title, ["type"] = row.Type, ["username"] = row.Username, ["unread"] = row.Unread, ["last_message"] = row.LastMessage };
    }
    private static async Task<JsonObject> MessageAsync(TelegramSession tg, TdApi.Message message) =>
        ConvertObject(await ExportSchema.BuildMessageAsync(tg, new ExportedMessage(message, message.ChatId, message.ChatId, message.Id), true));
    private static async Task<JsonArray> MessagesAsync(TelegramSession tg, IEnumerable<TdApi.Message> messages, CancellationToken token)
    {
        var rows = new JsonArray();
        foreach (var message in messages) { token.ThrowIfCancellationRequested(); rows.Add(await MessageAsync(tg, message)); }
        return rows;
    }

    internal static JsonObject ConvertObject(JObject value)
    {
        // The existing CLI schema keeps numeric IDs. Change only this independent MCP representation.
        value = (JObject)value.DeepClone();
        foreach (var property in value.Descendants().OfType<JProperty>().ToArray())
        {
            var key = property.Name.Replace("_", "").ToLowerInvariant();
            if (property.Value.Type == JTokenType.Integer && key.EndsWith("id") && key is not ("topicid" or "forumtopicid" or "fileid"))
                property.Value = Id(property.Value.Value<long>());
            if (key == "migrations" && property.Value is JArray ids)
                for (var i = 0; i < ids.Count; i++) if (ids[i].Type == JTokenType.Integer) ids[i] = Id(ids[i].Value<long>());
        }
        return JsonNode.Parse(value.ToString(Newtonsoft.Json.Formatting.None))!.AsObject();
    }
    private static JsonObject Page(JsonArray rows, string? cursor, bool more, bool complete, string reason) => new()
    { ["items"] = rows, ["next_cursor"] = cursor, ["has_more"] = more, ["complete"] = complete, ["termination_reason"] = reason };

    public ValueTask DisposeAsync() => _sessions.DisposeAsync();

}

internal sealed record McpCursor(string Fingerprint, long Message, int Date = 0, int Topic = 0)
{
    internal string Encode() => Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(this)));
    internal static McpCursor? Decode(string? text, string fingerprint)
    {
        if (text is null) return null;
        try
        {
            if (text.Length > 2048) throw new FormatException();
            var cursor = JsonSerializer.Deserialize<McpCursor>(Convert.FromBase64String(text));
            if (cursor is null || cursor.Fingerprint != fingerprint || cursor.Message < 0 || cursor.Date < 0 || cursor.Topic < 0) throw new FormatException();
            return cursor;
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        { throw new ArgumentException("Invalid cursor, or filters changed. Start a new query without cursor."); }
    }
}
