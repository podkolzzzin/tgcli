using System.Globalization;
using Newtonsoft.Json.Linq;
using TdLib;

namespace TgCli;

// Data operations shared by the console commands and MCP; no console writes.
internal static class TelegramOperations
{
    internal static Task<TdApi.Chats> ListChatsAsync(TelegramSession tg, int limit,
        CancellationToken cancellationToken = default, bool retainNativeOperation = true) =>
        ListChatsAsync(tg.Client, limit, cancellationToken, retainNativeOperation);

    internal static async Task<TdApi.Chats> ListChatsAsync(TdApi.IClient client, int limit,
        CancellationToken cancellationToken, bool retainNativeOperation = true) =>
        (await ListChatDetailsAsync(client, limit, cancellationToken, retainNativeOperation)).Chats;

    internal static Task<ChatListReadResult> ListChatDetailsAsync(TelegramSession tg, int limit,
        CancellationToken cancellationToken = default, bool retainNativeOperation = true) =>
        ListChatDetailsAsync(tg.Client, limit, cancellationToken, retainNativeOperation);

    private static async Task<ChatListReadResult> ListChatDetailsAsync(TdApi.IClient client, int limit,
        CancellationToken cancellationToken, bool retainNativeOperation)
    {
        ReadBounds.ValidatePage(0, 0, limit, 1);
        var main = new TdApi.ChatList.ChatListMain();
        var loadedCount = 0;
        var previousCount = -1;
        for (var page = 1; ; page++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var exhausted = false;
            try
            {
                await AwaitReadAsync(client.LoadChatsAsync(main, limit - loadedCount), cancellationToken, retainNativeOperation);
            }
            catch (TdException ex) when (ex.Error.Code == 404)
            {
                exhausted = true;
            }

            cancellationToken.ThrowIfCancellationRequested();
            var result = await AwaitReadAsync(client.GetChatsAsync(main, limit), cancellationToken, retainNativeOperation);
            loadedCount = result.ChatIds?.Length ?? 0;
            // A full slice cannot distinguish exactly this many chats from a longer loaded list.
            if (loadedCount >= limit) return new(result, false, "bounded_no_continuation", page, true);
            if (exhausted) return new(result, true, "chat_list_exhausted", page, false);
            if (loadedCount <= previousCount) return new(result, false, "chat_list_not_advanced", page, false);
            previousCount = loadedCount;
        }
    }

    internal static Task<TdApi.Chats> SearchChatsAsync(TelegramSession tg, string query, int limit, bool server = true,
        CancellationToken cancellationToken = default, bool retainNativeOperation = true) =>
        SearchChatsAsync(tg.Client, query, limit, server, cancellationToken, retainNativeOperation);

    internal static Task<TdApi.Chats> SearchChatsAsync(TdApi.IClient client, string query, int limit, bool server = true,
        CancellationToken cancellationToken = default, bool retainNativeOperation = true)
    {
        ReadBounds.ValidatePage(0, 0, limit, 1);
        if (string.IsNullOrWhiteSpace(query)) throw new ArgumentException("Search query must not be empty.", nameof(query));
        cancellationToken.ThrowIfCancellationRequested();
        return AwaitReadAsync(server ? client.SearchChatsOnServerAsync(query, limit) : client.SearchChatsAsync(query, limit),
            cancellationToken, retainNativeOperation);
    }

    internal static string NormalizeUsername(string username)
    {
        username = username.Trim();
        if (Uri.TryCreate(username, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http"
            && (uri.Host.Equals("t.me", StringComparison.OrdinalIgnoreCase) || uri.Host.Equals("telegram.me", StringComparison.OrdinalIgnoreCase)))
        {
            username = uri.AbsolutePath.Trim('/');
            if (uri.Query.Length > 0 || uri.Fragment.Length > 0)
                throw new ArgumentException("Provide a public username or its profile link.", nameof(username));
        }
        username = username.TrimStart('@');
        if (username.Length == 0 || username.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_'))
            throw new ArgumentException("Provide a public username or its profile link.", nameof(username));
        return username;
    }

    internal static Task<TdApi.Chat> ResolveChatAsync(TelegramSession tg, string username,
        CancellationToken cancellationToken = default, bool retainNativeOperation = true)
    {
        username = NormalizeUsername(username);
        cancellationToken.ThrowIfCancellationRequested();
        return AwaitReadAsync(tg.Client.SearchPublicChatAsync(username), cancellationToken, retainNativeOperation);
    }

    internal static async Task<JObject> StatsAsync(TelegramSession tg, long chatId, int topicId,
        string type, int maxPages, bool local, int requestTimeout, CancellationToken cancellationToken, bool retainNativeOperation = false)
    {
        if (topicId < 0)
        {
            throw new ArgumentException("--topic-id must be zero or greater.", nameof(topicId));
        }
        if (topicId > 0 && local)
        {
            throw new ArgumentException("--local is not supported with --topic-id.", nameof(local));
        }

        _ = AttachmentKinds.Parse(type);
        ReadBounds.ValidatePage(0, 0, 100, maxPages);
        ForumTopicHistory.ValidateRequestTimeout(requestTimeout);
        var topicHistory = topicId > 0
            ? await ForumTopicHistory.FetchAsync(
                tg,
                chatId,
                topicId,
                fromMessageId: 0,
                offset: 0,
                limit: 100,
                all: true,
                maxPages,
                requestTimeout,
                cancellationToken, retainNativeOperation)
            : null;
        var history = topicId == 0
            ? await ChatHistory.FetchAsync(tg, chatId, all: true, local, maxPages, followMigrations: true, cancellationToken, requestTimeout: requestTimeout, retainNativeOperation: retainNativeOperation)
            : null;
        cancellationToken.ThrowIfCancellationRequested();
        var messages = topicHistory is not null
            ? topicHistory.Messages.ToArray()
            : history!.Messages.Select(x => x.Message).ToArray();
        var complete = topicHistory?.Complete ?? history!.Complete;
        var terminationReason = topicHistory?.TerminationReason ?? history!.TerminationReason;
        var pagesFetched = topicHistory?.PagesFetched ?? history!.PagesFetched;
        var attachments = AttachmentStatistics.Build(messages, type);
        var payload = new JObject
        {
            ["chat_id"] = chatId,
            ["topic_id"] = topicId == 0 ? JValue.CreateNull() : topicId,
            ["count"] = messages.Length,
            ["count_kind"] = local ? "cache_count" : complete ? "exact" : "lower_bound",
            ["source"] = local ? "cache" : "telegram",
            ["freshness"] = local ? "cache" : topicId > 0 ? "topic_history" : "tdlib_update_state",
            ["complete"] = complete,
            ["termination_reason"] = terminationReason,
            ["pages_fetched"] = pagesFetched,
            ["first_timestamp"] = messages.Length == 0 ? JValue.CreateNull() : DateTimeOffset.FromUnixTimeSeconds(messages.Min(x => x.Date)).ToString("O"),
            ["last_timestamp"] = messages.Length == 0 ? JValue.CreateNull() : DateTimeOffset.FromUnixTimeSeconds(messages.Max(x => x.Date)).ToString("O"),
            ["participant_count"] = local ? JValue.CreateNull() : await ChatCommands.TryGetParticipantCountAsync(tg, chatId, cancellationToken, retainNativeOperation),
            ["attachments"] = attachments,
            ["migrations"] = new JArray(history?.SourceChats ?? [chatId])
        };

        return payload;
    }

    internal static async Task<List<TdApi.Message>> ContextAsync(TelegramSession tg, long chatId,
        long messageId, int before, int after, bool followReplyChain, CancellationToken cancellationToken) =>
        (await ContextDetailsAsync(tg, chatId, messageId, before, after, followReplyChain, cancellationToken,
            retainNativeOperation: true)).Messages;

    internal static Task<ContextReadResult> ContextDetailsAsync(TelegramSession tg, long chatId, long messageId,
        int before, int after, bool followReplyChain, CancellationToken cancellationToken, bool retainNativeOperation = false) =>
        FetchContextAsync(chatId, messageId, before, after, followReplyChain,
            request => AwaitReadAsync(tg.Client.ExecuteAsync(request), cancellationToken, retainNativeOperation),
            async (replyChatId, replyMessageId) =>
            {
                try
                {
                    return await AwaitReadAsync(tg.Client.GetMessageAsync(replyChatId, replyMessageId), cancellationToken, retainNativeOperation);
                }
                catch (TdException ex) when (IsMessageUnavailable(ex))
                {
                    return null;
                }
            }, cancellationToken);

    internal static async Task<ContextReadResult> FetchContextAsync(long chatId, long messageId, int before, int after,
        bool followReplyChain, Func<TdApi.GetChatHistory, Task<TdApi.Messages>> fetchHistory,
        Func<long, long, Task<TdApi.Message?>> getMessage, CancellationToken cancellationToken)
    {
        ReadBounds.ValidateContext(messageId, before, after);
        cancellationToken.ThrowIfCancellationRequested();
        var target = await getMessage(chatId, messageId)
            ?? throw new InvalidOperationException($"Message {messageId} is unavailable in chat {chatId}.");
        cancellationToken.ThrowIfCancellationRequested();

        var older = new SortedDictionary<long, TdApi.Message>();
        var newer = new SortedDictionary<long, TdApi.Message>();
        async Task<TdApi.Message[]> FetchWindow(long cursor, int offset, int limit)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var page = await fetchHistory(new TdApi.GetChatHistory
            {
                ChatId = chatId, FromMessageId = cursor, Offset = offset, Limit = limit, OnlyLocal = false
            });
            cancellationToken.ThrowIfCancellationRequested();
            return page.Messages_ ?? [];
        }

        // History is backwards-only except for the negative offset around the target.
        // Repeat a short newer window once so TDLib can finish loading its neighbours.
        if (after > 0)
        {
            var windowLimit = before + after + 1;
            for (var attempt = 0; attempt < 2 && newer.Count < after; attempt++)
            {
                foreach (var message in await FetchWindow(messageId, -after, windowLimit))
                {
                    if (message.Id < messageId) older.TryAdd(message.Id, message);
                    if (message.Id > messageId) newer.TryAdd(message.Id, message);
                }
            }
        }

        var olderCursor = older.Count == 0 ? messageId : older.First().Key;
        var olderExhausted = false;
        while (older.Count < before)
        {
            var raw = await FetchWindow(olderCursor, 0, Math.Min(100, before - older.Count + 1));
            if (raw.Length == 0)
            {
                olderExhausted = true;
                break;
            }

            var rows = ChatHistory.OlderHistoryRows(raw, olderCursor, continuation: true);
            if (rows.Length == 0) break;
            foreach (var message in rows) older.TryAdd(message.Id, message);
            olderCursor = rows[^1].Id;
        }

        var members = older.Reverse().Take(before).Select(x => x.Value).Append(target)
            .Concat(newer.Take(after).Select(x => x.Value)).ToDictionary(x => (x.ChatId, x.Id));
        var visited = new HashSet<(long, long)> { (target.ChatId, target.Id) };
        var chainStatus = followReplyChain ? "complete" : "disabled";
        (long ChatId, long MessageId)? unavailable = null;
        var current = target;
        for (var depth = 0; followReplyChain && current.ReplyTo is TdApi.MessageReplyTo.MessageReplyToMessage reply; depth++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var identity = (reply.ChatId == 0 ? current.ChatId : reply.ChatId, reply.MessageId);
            if (depth == 100 || !visited.Add(identity))
            {
                chainStatus = depth == 100 ? "depth_limit" : "cycle";
                break;
            }

            var ancestor = members.GetValueOrDefault(identity);
            if (ancestor is null && reply.MessageId > 0)
                ancestor = await getMessage(identity.Item1, identity.Item2);
            cancellationToken.ThrowIfCancellationRequested();
            if (ancestor is null)
            {
                chainStatus = "ancestor_unavailable";
                unavailable = identity;
                break;
            }

            members.TryAdd(identity, ancestor);
            current = ancestor;
        }

        return new(members.Values.OrderBy(x => x.Date).ThenBy(x => x.ChatId).ThenBy(x => x.Id).ToList(),
            older.Count >= before || olderExhausted, newer.Count >= after, Math.Min(older.Count, before),
            Math.Min(newer.Count, after), chainStatus, unavailable);
    }

    internal static bool IsMessageUnavailable(TdException ex) =>
        ex.Error.Code is 403 or 404 || ex.Error.Code == 400 &&
        (ex.Error.Message.Contains("message not found", StringComparison.OrdinalIgnoreCase)
            || ex.Error.Message.Contains("chat not found", StringComparison.OrdinalIgnoreCase)
            || ex.Error.Message.Contains("can't access", StringComparison.OrdinalIgnoreCase)
            || ex.Error.Message.Contains("MESSAGE_ID_INVALID", StringComparison.OrdinalIgnoreCase));

    internal static async Task<MessageSearchResult> SearchMessagePagesAsync(long fromMessageId, int offset,
        int limit, bool all, int maxPages, Func<long, int, int, Task<TdApi.FoundChatMessages>> fetch,
        Func<TdApi.Message, bool> matches, CancellationToken cancellationToken)
    {
        ReadBounds.ValidatePage(fromMessageId, offset, limit, maxPages, search: true);
        var messages = new List<TdApi.Message>();
        var seen = new HashSet<(long, long)>();
        var offsets = new HashSet<long> { fromMessageId };
        var cursor = fromMessageId;
        for (var page = 0; page < maxPages; page++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var response = await fetch(cursor, page == 0 ? offset : 0, all ? 100 : limit - messages.Count);
            cancellationToken.ThrowIfCancellationRequested();

            foreach (var message in response.Messages ?? [])
            {
                if (seen.Add((message.ChatId, message.Id)) && matches(message))
                    messages.Add(message);
            }

            var next = response.NextFromMessageId;
            if (next == 0)
                return new(messages, null, true, "search_exhausted", page + 1, messages.Count >= limit);
            if (next < 0 || !offsets.Add(next))
                return new(messages, null, false, "cursor_not_advanced", page + 1, messages.Count >= limit);
            if (!all && messages.Count >= limit)
                return new(messages, next, false, "page_limit", page + 1, true);
            cursor = next;
        }

        return new(messages, cursor, false, "max_pages_reached", maxPages, false);
    }

    // MCP's session gate retains the lease while native work settles.
    internal static async Task<T> AwaitReadAsync<T>(Task<T> task, CancellationToken token, bool retainNativeOperation)
    {
        var result = retainNativeOperation ? await task : await task.WaitAsync(token);
        token.ThrowIfCancellationRequested();
        return result;
    }

    internal static async Task<T> WithReadDeadlineAsync<T>(int seconds, CancellationToken cancellationToken,
        bool retainNativeOperation, Func<CancellationToken, Task<T>> operation)
    {
        if (retainNativeOperation) return await operation(cancellationToken);

        ForumTopicHistory.ValidateRequestTimeout(seconds);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(seconds));
        try
        {
            return await operation(deadline.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && deadline.IsCancellationRequested)
        {
            throw new TimeoutException($"Telegram read exceeded {seconds} seconds.");
        }
    }
}

internal sealed record ChatListReadResult(TdApi.Chats Chats, bool Complete, string TerminationReason,
    int PagesFetched, bool LimitSatisfied);

internal sealed record MessageSearchResult(List<TdApi.Message> Messages, long? NextMessageId,
    bool Complete, string TerminationReason, int PagesFetched, bool LimitSatisfied);

internal sealed record ContextReadResult(List<TdApi.Message> Messages, bool OlderComplete, bool NewerComplete,
    int OlderCount, int NewerCount, string ReplyChainStatus, (long ChatId, long MessageId)? UnavailableAncestor)
{
    public bool WindowComplete => OlderComplete && NewerComplete;
}

internal static class ReadBounds
{
    internal static void ValidatePage(long cursor, int offset, int limit, int maxPages, bool search = false)
    {
        if (cursor < 0) throw new ArgumentOutOfRangeException(nameof(cursor), "Use a TDLib message ID or zero.");
        if (limit is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(limit), "Limit must be from 1 to 100.");
        if (maxPages < 1) throw new ArgumentOutOfRangeException(nameof(maxPages), "Page budget must be positive.");
        if (offset is < -99 or > 0 || offset < -limit || (search && offset == -limit))
            throw new ArgumentOutOfRangeException(nameof(offset), "Offset must be between -99 and 0 and within the requested page limit.");
        if (cursor == 0 && offset != 0)
            throw new ArgumentException("A nonzero offset requires a TDLib start message ID.", nameof(offset));
    }

    internal static void ValidateContext(long messageId, int before, int after)
    {
        if (messageId <= 0) throw new ArgumentOutOfRangeException(nameof(messageId), "Message ID must be positive.");
        if (before is < 0 or > 99 || after is < 0 or > 99 || before + after > 99)
            throw new ArgumentException("Context requires nonnegative before and after with a combined maximum of 99.");
    }

    internal static (DateTimeOffset? Since, DateTimeOffset? Until) ParseDates(string? since, string? until)
    {
        var from = ParseDate(since, false);
        var through = ParseDate(until, true);
        if (from > through) throw new ArgumentException("Since date must be at or before until date.");
        return (from, through);
    }

    private static DateTimeOffset? ParseDate(string? value, bool endOfDay)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (DateOnly.TryParseExact(value.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
        {
            var time = endOfDay ? TimeOnly.MaxValue : TimeOnly.MinValue;
            return new DateTimeOffset(day.ToDateTime(time), TimeSpan.Zero);
        }
        string[] formats = ["yyyy-MM-dd'T'HH:mmK", "yyyy-MM-dd'T'HH:mm:ssK", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK"];
        if (DateTimeOffset.TryParseExact(value.Trim(), formats, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var timestamp))
            return timestamp;
        throw new ArgumentException($"Invalid date '{value}'. Use YYYY-MM-DD or an ISO 8601 timestamp.");
    }
}
