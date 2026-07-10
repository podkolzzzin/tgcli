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
    public int PagesFetched { get; set; }
    public int Duplicates { get; set; }
    public int Retries { get; set; }
    public int InaccessibleMessages { get; set; }
    public string TerminationReason { get; set; } = "history_exhausted";
    public Dictionary<long, int> PagesByChat { get; } = [];
    public Dictionary<long, long?> OldestReachableByChat { get; } = [];
    public List<JObject> Gaps { get; } = [];
    public bool Complete { get; set; } = true;
}

internal static class ChatHistory
{
    private const int ChannelMessageIdShift = 20;

    public static async Task<HistoryFetchResult> FetchAsync(TelegramSession tg, long chatId, bool all, bool local, int maxPages, bool followMigrations)
    {
        var result = new HistoryFetchResult();
        var chain = followMigrations ? await GetMigrationChainAsync(tg, chatId) : [chatId];
        var seen = new HashSet<(long ChatId, long MessageId)>();

        foreach (var sourceChatId in chain)
        {
            result.SourceChats.Add(sourceChatId);
            var isChannel = await IsChannelAsync(tg, sourceChatId);
            var cursor = 0L;
            var exhausted = false;
            var pageLimit = all ? Math.Max(1, maxPages) : 1;
            for (var page = 0; page < pageLimit; page++)
            {
                TdApi.Messages history;
                var attempt = 0;
                while (true)
                {
                    try
                    {
                        history = await tg.Client.GetChatHistoryAsync(sourceChatId, cursor, offset: 0, limit: 100, onlyLocal: local);
                        break;
                    }
                    catch (Exception ex) when (attempt++ < 2)
                    {
                        result.Retries++;
                        result.Warnings.Add($"Retry {attempt}/2 for chat {sourceChatId} cursor {cursor}: {ex.Message}");
                        await Task.Delay(TimeSpan.FromMilliseconds(250 * attempt));
                    }
                }
                result.PagesFetched++;
                result.PagesByChat[sourceChatId] = result.PagesByChat.GetValueOrDefault(sourceChatId) + 1;
                if (history.Messages_.Length == 0)
                {
                    if (isChannel && TryMoveToPreviousChannelAnchor(ref cursor))
                    {
                        if (StopAtMaxPages(result, sourceChatId, maxPages, page, pageLimit))
                        {
                            break;
                        }

                        continue;
                    }

                    exhausted = true;
                    break;
                }

                var newMessages = 0;
                foreach (var message in history.Messages_)
                {
                    if (!seen.Add((message.ChatId, message.Id)))
                    {
                        result.Duplicates++;
                        continue;
                    }

                    result.Messages.Add(new ExportedMessage(message, sourceChatId, message.ChatId, message.Id));
                    newMessages++;
                }

                var oldestMessageId = history.Messages_.Min(x => x.Id);
                cursor = oldestMessageId;
                if (newMessages == 0 && isChannel && TryMoveToPreviousChannelAnchor(ref cursor))
                {
                    if (StopAtMaxPages(result, sourceChatId, maxPages, page, pageLimit))
                    {
                        break;
                    }

                    continue;
                }

                if (!all || history.Messages_.Length < 100)
                {
                    if (isChannel && GetShortMessageId(oldestMessageId) > 1)
                    {
                        result.Warnings.Add($"Short channel history page ({history.Messages_.Length} messages) at public id {GetShortMessageId(oldestMessageId)}; probing older public ids.");
                        if (StopAtMaxPages(result, sourceChatId, maxPages, page, pageLimit))
                        {
                            break;
                        }

                        continue;
                    }

                    exhausted = all;
                    break;
                }

                if (StopAtMaxPages(result, sourceChatId, maxPages, page, pageLimit))
                {
                    break;
                }
            }

            result.OldestReachableByChat[sourceChatId] = result.Messages
                .Where(x => x.SourceChatId == sourceChatId)
                .Select(x => (long?)x.Message.Id)
                .Min();
            if (all && !exhausted && result.Complete)
            {
                result.Complete = false;
                result.TerminationReason = local ? "local_database_limit" : "client_limit";
            }
        }

        result.Messages.Sort((a, b) =>
        {
            var byDate = a.Message.Date.CompareTo(b.Message.Date);
            return byDate != 0 ? byDate : a.Message.Id.CompareTo(b.Message.Id);
        });
        return result;
    }

    private static bool StopAtMaxPages(HistoryFetchResult result, long sourceChatId, int maxPages, int page, int pageLimit)
    {
        if (page != pageLimit - 1)
        {
            return false;
        }

        result.Complete = false;
        result.TerminationReason = "max_pages_reached";
        result.Warnings.Add($"Stopped at --max-pages={maxPages} for chat {sourceChatId}; history may be incomplete.");
        return true;
    }

    public static long GetShortMessageId(long messageId) => messageId >> ChannelMessageIdShift;

    public static long ToChannelMessageId(long shortMessageId) => shortMessageId << ChannelMessageIdShift;

    private static bool TryMoveToPreviousChannelAnchor(ref long cursor)
    {
        var shortMessageId = GetShortMessageId(cursor);
        if (shortMessageId <= 1)
        {
            return false;
        }

        cursor = ToChannelMessageId(shortMessageId - 1);
        return true;
    }

    private static async Task<bool> IsChannelAsync(TelegramSession tg, long chatId)
    {
        try
        {
            var chat = await tg.Client.GetChatAsync(chatId);
            return chat.Type is TdApi.ChatType.ChatTypeSupergroup { IsChannel: true };
        }
        catch
        {
            return false;
        }
    }

    public static async Task<IReadOnlyList<long>> GetMigrationChainAsync(TelegramSession tg, long chatId)
    {
        // First walk forward when the caller supplied the legacy basic-group chat.
        var initial = await tg.Client.GetChatAsync(chatId);
        if (GetLongProperty(initial.Type, "BasicGroupId") is long initialBasicGroupId)
        {
            var basicInfo = await tg.Client.GetBasicGroupFullInfoAsync(initialBasicGroupId);
            if (GetLongProperty(basicInfo, "UpgradedToSupergroupId") is long upgradedTo and not 0)
            {
                var upgraded = await tg.Client.CreateSupergroupChatAsync(upgradedTo, force: false);
                chatId = upgraded.Id;
            }
        }

        // Then walk backward from the newest supergroup, producing oldest -> newest.
        var ordered = new List<long>();
        var seen = new HashSet<long>();
        var current = chatId;

        while (seen.Add(current))
        {
            var chat = await tg.Client.GetChatAsync(current);
            ordered.Add(current);

            var type = chat.Type;
            var supergroupId = GetLongProperty(type, "SupergroupId");
            if (supergroupId is null)
            {
                break;
            }

            var fullInfo = await tg.Client.GetSupergroupFullInfoAsync(supergroupId.Value);
            var upgradedFromBasicGroupId = GetLongProperty(fullInfo, "UpgradedFromBasicGroupId");
            if (upgradedFromBasicGroupId is null or 0)
            {
                break;
            }

            var legacyChat = await tg.Client.CreateBasicGroupChatAsync(upgradedFromBasicGroupId.Value, force: false);
            current = legacyChat.Id;
        }

        ordered.Reverse();
        return ordered;
    }

    public static JObject BuildManifest(HistoryFetchResult history)
    {
        var messages = history.Messages;
        var dates = messages.Select(x => x.Message.Date).Where(x => x > 0).ToArray();
        var publicIds = messages
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
            ["complete"] = history.Complete
        };

        if (publicIds.Length > 0)
        {
            var firstPost = messages.Where(x => x.Message.Date > 0).MinBy(x => x.Message.Date) ?? messages.MinBy(x => x.Message.Id)!;
            var lastPost = messages.Where(x => x.Message.Date > 0).MaxBy(x => x.Message.Date) ?? messages.MaxBy(x => x.Message.Id)!;
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
