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
    public bool Complete { get; set; } = true;
}

internal static class ChatHistory
{
    public static async Task<HistoryFetchResult> FetchAsync(TelegramSession tg, long chatId, bool all, bool local, int maxPages, bool followMigrations)
    {
        var result = new HistoryFetchResult();
        var chain = followMigrations ? await GetMigrationChainAsync(tg, chatId) : [chatId];
        var seen = new HashSet<(long ChatId, long MessageId)>();

        foreach (var sourceChatId in chain)
        {
            result.SourceChats.Add(sourceChatId);
            var cursor = 0L;
            for (var page = 0; page < (all ? Math.Max(1, maxPages) : 1); page++)
            {
                var history = await tg.Client.GetChatHistoryAsync(sourceChatId, cursor, offset: 0, limit: 100, onlyLocal: local);
                result.PagesFetched++;
                if (history.Messages_.Length == 0)
                {
                    break;
                }

                foreach (var message in history.Messages_)
                {
                    if (!seen.Add((message.ChatId, message.Id)))
                    {
                        result.Duplicates++;
                        continue;
                    }

                    result.Messages.Add(new ExportedMessage(message, sourceChatId, message.ChatId, message.Id));
                }

                cursor = history.Messages_.Min(x => x.Id);
                if (!all || history.Messages_.Length < 100)
                {
                    break;
                }

                if (page == Math.Max(1, maxPages) - 1)
                {
                    result.Complete = false;
                    result.Warnings.Add($"Stopped at --max-pages={maxPages} for chat {sourceChatId}; history may be incomplete.");
                }
            }
        }

        result.Messages.Sort((a, b) => a.Message.Id.CompareTo(b.Message.Id));
        return result;
    }

    public static async Task<IReadOnlyList<long>> GetMigrationChainAsync(TelegramSession tg, long chatId)
    {
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
        return new JObject
        {
            ["message_count"] = messages.Count,
            ["first_date"] = dates.Length == 0 ? JValue.CreateNull() : FormatDate(dates.Min()),
            ["last_date"] = dates.Length == 0 ? JValue.CreateNull() : FormatDate(dates.Max()),
            ["source_chats"] = new JArray(history.SourceChats.Distinct()),
            ["pages_fetched"] = history.PagesFetched,
            ["duplicates"] = history.Duplicates,
            ["warnings"] = new JArray(history.Warnings),
            ["complete"] = history.Complete
        };
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

