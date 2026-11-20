using System.Globalization;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using TdLib;

namespace TgCli;

internal sealed record ExportedMessage(TdApi.Message Message, long SourceChatId, long OriginalChatId, long OriginalMessageId);

internal sealed class HistoryFetchResult
{
    public List<ExportedMessage> Messages { get; } = [];
    public List<long> SourceChats { get; } = [];
    public List<string> Warnings { get; } = [];
    public HashSet<long> ChannelChats { get; } = [];
    public int PagesFetched { get; set; }
    public int Duplicates { get; set; }
    public int Retries { get; set; }
    public int InaccessibleMessages { get; set; }
    public string TerminationReason { get; set; } = "history_exhausted";
    public Dictionary<long, int> PagesByChat { get; } = [];
    public Dictionary<long, long?> OldestReachableByChat { get; } = [];
    public List<JObject> Gaps { get; } = [];
    public bool Complete { get; set; } = true;
    public bool Local { get; init; }
    public bool LimitSatisfied { get; set; }
    public long? NextMessageId { get; set; }
}

internal static class ChatHistory
{
    private const int ChannelMessageIdShift = 20;

    public static async Task<HistoryFetchResult> FetchAsync(
        TelegramSession tg, long chatId, bool all, bool local, int maxPages, bool followMigrations,
        CancellationToken cancellationToken = default, long fromMessageId = 0, int offset = 0, int limit = 100,
        Func<TdApi.Message, bool>? match = null, int requestTimeout = 60, bool retainNativeOperation = false)
    {
        ReadBounds.ValidatePage(fromMessageId, offset, limit, maxPages);
        ForumTopicHistory.ValidateRequestTimeout(requestTimeout);
        return await TelegramOperations.WithReadDeadlineAsync(requestTimeout, cancellationToken, retainNativeOperation, async token =>
        {
            // Local-only reads avoid remote migration enrichment.
            var chain = followMigrations && !local ? await GetMigrationChainAsync(tg, chatId, token, retainNativeOperation) : [chatId];
            return await FetchPagesAsync(chain, all, local, maxPages,
                request => tg.Client.ExecuteAsync(request), id => tg.Client.GetChatAsync(id),
                token, fromMessageId, offset, limit, match, retainNativeOperation);
        });
    }

    internal static async Task<HistoryFetchResult> FetchPagesAsync(
        IReadOnlyList<long> chain, bool all, bool local, int maxPages,
        Func<TdApi.GetChatHistory, Task<TdApi.Messages>> fetchPage, Func<long, Task<TdApi.Chat>> getChat,
        CancellationToken cancellationToken = default, long fromMessageId = 0, int offset = 0, int limit = 100,
        Func<TdApi.Message, bool>? match = null, bool retainNativeOperation = false)
    {
        ReadBounds.ValidatePage(fromMessageId, offset, limit, maxPages);
        if (chain.Count > 1 && (fromMessageId != 0 || offset != 0))
            throw new ArgumentException("A history start cursor applies to a single chat.");

        var result = new HistoryFetchResult { Local = local, LimitSatisfied = !all };
        var seen = new HashSet<(long ChatId, long MessageId)>();

        foreach (var sourceChatId in chain)
        {
            cancellationToken.ThrowIfCancellationRequested();
            result.SourceChats.Add(sourceChatId);
            var chat = await TelegramOperations.AwaitReadAsync(getChat(sourceChatId), cancellationToken, retainNativeOperation);
            if (chat.Type is TdApi.ChatType.ChatTypeSupergroup { IsChannel: true })
                result.ChannelChats.Add(sourceChatId);

            var cursor = fromMessageId;
            var matches = 0;
            var oldest = (long?)null;
            var reason = "max_pages_reached";
            var acceptedEmptyRetry = false;
            for (var page = 0; page < maxPages; page++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var request = new TdApi.GetChatHistory
                {
                    ChatId = sourceChatId,
                    FromMessageId = cursor,
                    Offset = page == 0 ? offset : 0,
                    Limit = all ? 100 : Math.Min(100, limit - matches + (cursor != 0 ? 1 : 0)),
                    OnlyLocal = local
                };
                TdApi.Messages history;
                var attempt = 0;
                while (true)
                {
                    try
                    {
                        history = await TelegramOperations.AwaitReadAsync(fetchPage(request), cancellationToken, retainNativeOperation);
                        break;
                    }
                    catch (TdException ex) when (ex.Error.Code >= 500 && attempt++ < 2)
                    {
                        result.Retries++;
                        await Task.Delay(TimeSpan.FromMilliseconds(250 * attempt), cancellationToken);
                    }
                }

                result.PagesFetched++;
                result.PagesByChat[sourceChatId] = result.PagesByChat.GetValueOrDefault(sourceChatId) + 1;
                var raw = history.Messages_ ?? [];
                if (!local && fromMessageId == 0 && offset == 0 && oldest is null
                    && chat.LastMessage?.Id is long knownLatest && (raw.Length == 0 || raw.Max(x => x.Id) < knownLatest))
                {
                    reason = "newest_message_unavailable";
                    // Compare with the latest message known after TDLib synchronization. This is not a server snapshot.
                    if (!acceptedEmptyRetry && page + 1 < maxPages)
                    {
                        acceptedEmptyRetry = true;
                        result.Retries++;
                        continue;
                    }
                    break;
                }

                if (raw.Length == 0)
                {
                    reason = local ? "local_cache_exhausted" : "history_exhausted";
                    result.NextMessageId = null;
                    break;
                }

                var rows = OlderHistoryRows(raw, cursor, continuation: cursor != 0 && (page != 0 || offset == 0));
                result.Duplicates += raw.Length - rows.Length;
                if (rows.Length == 0)
                {
                    reason = "cursor_not_advanced";
                    result.NextMessageId = null;
                    break;
                }

                foreach (var message in rows)
                {
                    if (!seen.Add((message.ChatId, message.Id)))
                    {
                        result.Duplicates++;
                        continue;
                    }

                    oldest = message.Id;
                    if (match is null || match(message))
                    {
                        result.Messages.Add(new ExportedMessage(message, sourceChatId, message.ChatId, message.Id));
                        matches++;
                    }
                    if (!all && matches >= limit)
                        break;
                }

                // The next request must use an actual TDLib ID, even when every row was filtered out.
                if (oldest is not long next || (cursor != 0 && next >= cursor && request.Offset == 0))
                {
                    reason = "cursor_not_advanced";
                    result.NextMessageId = null;
                    break;
                }
                cursor = next;
                result.NextMessageId = cursor;

                if (!all && matches >= limit)
                {
                    reason = "page_limit";
                    break;
                }
            }

            result.OldestReachableByChat[sourceChatId] = oldest;
            result.LimitSatisfied &= matches >= limit;
            if (reason != "history_exhausted")
            {
                result.Complete = false;
                result.TerminationReason = reason;
                if (reason is "max_pages_reached" or "cursor_not_advanced" or "newest_message_unavailable")
                    result.Warnings.Add($"Chat {sourceChatId} history ended with {reason} after {result.PagesByChat[sourceChatId]} pages.");
            }
        }

        if (chain.Count != 1) result.NextMessageId = null;
        result.Messages.Sort((a, b) =>
        {
            var byDate = a.Message.Date.CompareTo(b.Message.Date);
            if (byDate != 0) return byDate;
            var byChat = a.SourceChatId.CompareTo(b.SourceChatId);
            return byChat != 0 ? byChat : a.Message.Id.CompareTo(b.Message.Id);
        });
        return result;
    }

    internal static TdApi.Message[] OlderHistoryRows(TdApi.Message[] rows, long anchor, bool continuation) =>
        rows.Where(message => !continuation || message.Id < anchor)
            .DistinctBy(message => (message.ChatId, message.Id))
            .OrderByDescending(message => message.Id)
            .ToArray();

    public static long GetShortMessageId(long messageId) => messageId >> ChannelMessageIdShift;

    internal static long? TryGetPublicPostId(long messageId)
    {
        var postId = GetShortMessageId(messageId);
        return postId is > 0 and <= int.MaxValue && (postId << ChannelMessageIdShift) == messageId ? postId : null;
    }

    public static long ToChannelMessageId(long shortMessageId)
    {
        if (shortMessageId is < 1 or > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(shortMessageId), "A public post ID must be between 1 and 2147483647.");
        return shortMessageId << ChannelMessageIdShift;
    }

    public static Task<IReadOnlyList<long>> GetMigrationChainAsync(
        TelegramSession tg, long chatId, CancellationToken cancellationToken = default, bool retainNativeOperation = false) =>
        GetMigrationChainAsync(tg.Client, chatId, cancellationToken, retainNativeOperation);

    internal static async Task<IReadOnlyList<long>> GetMigrationChainAsync(TdApi.IClient client,
        long chatId, CancellationToken cancellationToken, bool retainNativeOperation = false)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var chat = await TelegramOperations.AwaitReadAsync(client.GetChatAsync(chatId), cancellationToken, retainNativeOperation);
        var initialBasic = chat.Type as TdApi.ChatType.ChatTypeBasicGroup;
        if (initialBasic is not null)
        {
            var group = await TelegramOperations.AwaitReadAsync(client.GetBasicGroupAsync(initialBasic.BasicGroupId), cancellationToken, retainNativeOperation);
            if (group.UpgradedToSupergroupId == 0) return [chat.Id];
            var successor = await TelegramOperations.AwaitReadAsync(client.CreateSupergroupChatAsync(group.UpgradedToSupergroupId, force: false), cancellationToken, retainNativeOperation);
            return [chat.Id, successor.Id];
        }

        if (chat.Type is not TdApi.ChatType.ChatTypeSupergroup { IsChannel: false } supergroup)
            return [chat.Id];

        var info = await TelegramOperations.AwaitReadAsync(client.GetSupergroupFullInfoAsync(supergroup.SupergroupId), cancellationToken, retainNativeOperation);
        if (info.UpgradedFromBasicGroupId == 0) return [chat.Id];
        var predecessor = await TelegramOperations.AwaitReadAsync(client.CreateBasicGroupChatAsync(info.UpgradedFromBasicGroupId, force: false), cancellationToken, retainNativeOperation);
        return [predecessor.Id, chat.Id];
    }

    public static JObject BuildManifest(HistoryFetchResult history)
    {
        var messages = history.Messages;
        var dates = messages.Select(x => x.Message.Date).Where(x => x > 0).ToArray();
        var publicIds = messages
            .Where(x => history.ChannelChats.Contains(x.SourceChatId))
            .Select(x => GetShortMessageId(x.Message.Id))
            .Where(x => x > 0)
            .Distinct()
            .Order()
            .ToArray();
        var missingPublicIds = GetMissingPublicIds(publicIds);
        var manifest = new JObject
        {
            ["message_count"] = messages.Count,
            ["fetched_count"] = messages.Count,
            ["first_date"] = dates.Length == 0 ? JValue.CreateNull() : FormatDate(dates.Min()),
            ["last_date"] = dates.Length == 0 ? JValue.CreateNull() : FormatDate(dates.Max()),
            ["source_chats"] = new JArray(history.SourceChats.Distinct()),
            ["pages_fetched"] = history.PagesFetched,
            ["pages_by_chat"] = JObject.FromObject(history.PagesByChat),
            ["duplicates"] = history.Duplicates,
            ["retries"] = history.Retries,
            ["gaps"] = new JArray(history.Gaps),
            ["inaccessible_messages"] = history.InaccessibleMessages,
            ["oldest_reachable_message_by_chat"] = JObject.FromObject(history.OldestReachableByChat),
            ["newest_fetched_message"] = messages.Count == 0 ? JValue.CreateNull() : new JObject
            {
                ["chat_id"] = messages.MaxBy(x => x.Message.Date)!.Message.ChatId,
                ["message_id"] = messages.MaxBy(x => x.Message.Date)!.Message.Id
            },
            ["termination_reason"] = history.TerminationReason,
            ["warnings"] = new JArray(history.Warnings),
            ["source"] = history.Local ? "cache" : "telegram",
            ["freshness"] = history.Local ? "cache" : "tdlib_update_state",
            ["complete"] = history.Complete
        };

        if (publicIds.Length > 0)
        {
            var channelMessages = messages.Where(x => history.ChannelChats.Contains(x.SourceChatId)).ToArray();
            var firstPost = channelMessages.Where(x => x.Message.Date > 0).MinBy(x => x.Message.Date) ?? channelMessages.MinBy(x => x.Message.Id)!;
            var lastPost = channelMessages.Where(x => x.Message.Date > 0).MaxBy(x => x.Message.Date) ?? channelMessages.MaxBy(x => x.Message.Id)!;
            var latestPublicId = publicIds[^1];
            var missing = missingPublicIds.ToArray();
            manifest["public_id_range"] = new JObject
            {
                ["first"] = publicIds[0],
                ["last"] = latestPublicId,
                ["missing_count"] = missing.Length,
                ["missing_public_ids"] = new JArray(missing)
            };
            manifest["first_post"] = ToPostBoundary(firstPost);
            manifest["last_post"] = ToPostBoundary(lastPost);

            if (latestPublicId > messages.Count && latestPublicId - messages.Count > Math.Max(10, latestPublicId / 20))
            {
                history.Warnings.Add($"Latest public id {latestPublicId} is much higher than exported count {messages.Count}; inspect public_id_range.missing_public_ids for gaps.");
                ((JArray)manifest["warnings"]!).Add(history.Warnings[^1]);
            }
        }

        return manifest;
    }

    private static JObject ToPostBoundary(ExportedMessage item) => new()
    {
        ["chat_id"] = item.Message.ChatId,
        ["message_id"] = item.Message.Id,
        ["public_id"] = GetShortMessageId(item.Message.Id),
        ["date"] = item.Message.Date == 0 ? JValue.CreateNull() : FormatDate(item.Message.Date)
    };

    private static IEnumerable<long> GetMissingPublicIds(long[] publicIds)
    {
        if (publicIds.Length < 2)
        {
            yield break;
        }

        var present = publicIds.ToHashSet();
        for (var id = publicIds[0]; id <= publicIds[^1]; id++)
        {
            if (!present.Contains(id))
            {
                yield return id;
            }
        }
    }

    public static void ValidateManifest(JObject manifest, DateTimeOffset? expectSince, int expectCountMin, bool failIncomplete)
    {
        var failures = new List<string>();
        if (expectCountMin > 0 && manifest.Value<int>("message_count") < expectCountMin)
        {
            failures.Add($"message_count is below --expect-count-min={expectCountMin}");
        }

        if (expectSince is not null && DateTimeOffset.TryParse(manifest.Value<string>("first_date"), out var first) && first > expectSince)
        {
            failures.Add($"first_date is after --expect-since={expectSince:O}");
        }

        if (failIncomplete && manifest.Value<bool>("complete") == false)
        {
            failures.Add("export is incomplete");
        }

        if (failures.Count > 0)
        {
            throw new InvalidOperationException("Export integrity check failed: " + string.Join("; ", failures));
        }
    }

    public static long? GetLongProperty(object? value, string name)
    {
        var propertyValue = value?.GetType().GetProperty(name)?.GetValue(value);
        return propertyValue switch
        {
            long l => l,
            int i => i,
            _ => null
        };
    }

    private static string FormatDate(int unixSeconds)
    {
        return DateTimeOffset.FromUnixTimeSeconds(unixSeconds).ToString("O", CultureInfo.InvariantCulture);
    }
}
