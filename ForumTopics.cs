using System.ComponentModel.DataAnnotations;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using TdLib;

namespace TgCli;

internal sealed class ForumTopicHistoryResult
{
    public List<TdApi.Message> Messages { get; } = [];
    public int PagesFetched { get; set; }
    public bool Complete { get; set; }
    public bool LimitSatisfied { get; set; }
    public long? NextMessageId { get; set; }
    public string TerminationReason { get; set; } = "not_started";
}

internal static class ForumTopicHistory
{
    private const int PageSize = 100;

    public static Task<ForumTopicHistoryResult> FetchAsync(
        TelegramSession tg,
        long chatId,
        int topicId,
        long fromMessageId,
        int offset,
        int limit,
        bool all,
        int maxPages,
        int requestTimeout,
        CancellationToken cancellationToken, bool retainNativeOperation = false, Func<TdApi.Message, bool>? match = null)
    {
        ValidateTopicId(topicId);
        ValidateRequestTimeout(requestTimeout);

        return TelegramOperations.WithReadDeadlineAsync(requestTimeout, cancellationToken, retainNativeOperation, token =>
            FetchAsync(chatId, topicId, fromMessageId, offset, limit, all, maxPages,
                async (request, ct) => await TelegramOperations.AwaitReadAsync(tg.Client.ExecuteAsync(request), ct, retainNativeOperation),
                token, match));
    }

    internal static async Task<ForumTopicHistoryResult> FetchAsync(
        long chatId,
        int topicId,
        long fromMessageId,
        int offset,
        int limit,
        bool all,
        int maxPages,
        Func<TdApi.GetForumTopicHistory, CancellationToken, Task<TdApi.Messages>> fetchPage,
        CancellationToken cancellationToken, Func<TdApi.Message, bool>? match = null)
    {
        ValidateTopicId(topicId);
        ReadBounds.ValidatePage(fromMessageId, offset, limit, maxPages);
        var result = new ForumTopicHistoryResult { TerminationReason = "max_pages_reached" };
        var cursor = fromMessageId;

        for (var page = 0; page < maxPages; page++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var response = await fetchPage(new TdApi.GetForumTopicHistory
            {
                ChatId = chatId,
                ForumTopicId = topicId,
                FromMessageId = cursor,
                Offset = page == 0 ? offset : 0,
                Limit = all ? PageSize : Math.Min(PageSize, limit - result.Messages.Count + (cursor != 0 ? 1 : 0))
            }, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            result.PagesFetched++;

            var raw = response.Messages_ ?? [];
            if (raw.Length == 0)
            {
                result.Complete = true;
                result.TerminationReason = "history_exhausted";
                result.NextMessageId = null;
                break;
            }

            var rows = ChatHistory.OlderHistoryRows(raw, cursor, cursor != 0 && (page != 0 || offset == 0));
            if (rows.Length == 0)
            {
                result.TerminationReason = "cursor_stalled";
                result.NextMessageId = null;
                break;
            }

            foreach (var message in rows)
            {
                cursor = message.Id;
                if (match is null || match(message)) result.Messages.Add(message);
                if (!all && result.Messages.Count >= limit)
                {
                    result.LimitSatisfied = true;
                    result.TerminationReason = "page_limit";
                    break;
                }
            }

            result.NextMessageId = cursor;
            if (result.LimitSatisfied) break;
        }

        return result;
    }

    public static void ValidateTopicId(int topicId)
    {
        if (topicId <= 0)
        {
            throw new ValidationException("Forum topic id must be greater than zero.");
        }
    }

    public static void ValidateRequestTimeout(int requestTimeout)
    {
        if (requestTimeout <= 0 || requestTimeout > (uint.MaxValue - 1L) / 1000)
        {
            throw new ValidationException("Request timeout is outside the supported range.");
        }
    }
}

internal static class AttachmentStatistics
{
    public static JObject Build(IEnumerable<TdApi.Message> messages, string type)
    {
        var requestedKind = AttachmentKinds.Parse(type);
        var files = requestedKind is AttachmentKind.All or AttachmentKind.File
            ? messages.SelectMany(MessageFiles.GetAllFiles).ToArray()
            : messages
                .Select(message => MessageFiles.GetPrimaryAttachment(message.Content, type))
                .Where(file => file is not null)
                .Cast<MessageFile>()
                .ToArray();

        var known = files.Where(file => MessageFiles.GetEffectiveSize(file) is not null).ToArray();
        var unique = files
            .GroupBy(FileIdentity, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToArray();
        var uniqueKnown = unique.Where(file => MessageFiles.GetEffectiveSize(file) is not null).ToArray();

        return new JObject
        {
            ["type"] = AttachmentKindName(requestedKind),
            ["count"] = files.Length,
            ["known_size_count"] = known.Length,
            ["unknown_size_count"] = files.Length - known.Length,
            ["total_size_bytes"] = known.Sum(file => MessageFiles.GetEffectiveSize(file)!.Value),
            ["unique_count"] = unique.Length,
            ["unique_total_size_bytes"] = uniqueKnown.Sum(file => MessageFiles.GetEffectiveSize(file)!.Value),
            ["by_kind"] = JObject.FromObject(files
                .GroupBy(file => file.Kind)
                .ToDictionary(
                    group => group.Key,
                    group => new
                    {
                        count = group.Count(),
                        total_size_bytes = group
                            .Select(MessageFiles.GetEffectiveSize)
                            .Where(size => size is not null)
                            .Sum(size => size!.Value)
                    }))
        };
    }

    private static string FileIdentity(MessageFile file)
    {
        if (!string.IsNullOrWhiteSpace(file.UniqueId))
        {
            return $"unique:{file.UniqueId}";
        }

        if (!string.IsNullOrWhiteSpace(file.RemoteId))
        {
            return $"remote:{file.RemoteId}";
        }

        return $"file:{file.FileId}";
    }

    private static string AttachmentKindName(AttachmentKind kind)
    {
        return kind switch
        {
            AttachmentKind.VideoNote => "video-note",
            AttachmentKind.Document => "document",
            _ => kind.ToString().ToLowerInvariant()
        };
    }
}

public sealed class ForumCommands
{
    /// <summary>
    /// List forum topics in a supergroup.
    /// </summary>
    public async Task Topics(
        long chatId,
        string query = "",
        int limit = 100,
        bool all = false,
        int maxPages = 100,
        string format = "jsonl",
        int requestTimeout = 60,
        int lockTimeout = 30,
        bool noWait = false,
        string? session = null,
        CancellationToken cancellationToken = default)
    {
        var parsedFormat = format.Trim().ToLowerInvariant();
        if (parsedFormat is not ("json" or "jsonl" or "tsv" or "plain"))
            throw new ArgumentException("Format must be one of: json, jsonl, tsv, plain.", nameof(format));
        ReadBounds.ValidatePage(0, 0, limit, maxPages);
        ForumTopicHistory.ValidateRequestTimeout(requestTimeout);
        cancellationToken.ThrowIfCancellationRequested();
        await using var tg = await TelegramSession.CreateReadyAsync(session, lockTimeout, noWait);
        var result = await TelegramOperations.WithReadDeadlineAsync(requestTimeout, cancellationToken, false, token =>
            FetchTopicsAsync(chatId, query, limit, all, maxPages, request => tg.Client.ExecuteAsync(request).WaitAsync(token), token));

        var rows = result.Topics.Select(topic => new JObject
        {
            ["chat_id"] = topic.Info.ChatId,
            ["topic_id"] = topic.Info.ForumTopicId,
            ["name"] = topic.Info.Name,
            ["is_general"] = topic.Info.IsGeneral,
            ["is_closed"] = topic.Info.IsClosed,
            ["is_hidden"] = topic.Info.IsHidden,
            ["is_pinned"] = topic.IsPinned,
            ["unread_count"] = topic.UnreadCount,
            ["last_message_id"] = topic.LastMessage?.Id ?? 0
        }).ToArray();

        if (parsedFormat != "json" && !result.Complete)
            Console.Error.WriteLine($"Forum topic coverage: {result.TerminationReason} after {result.PagesFetched} pages.");

        switch (parsedFormat)
        {
            case "json":
                Console.WriteLine(new JObject
                {
                    ["items"] = new JArray(rows),
                    ["complete"] = result.Complete,
                    ["termination_reason"] = result.TerminationReason,
                    ["pages_fetched"] = result.PagesFetched,
                    ["next_offset"] = result.Next is { } next ? new JObject
                    {
                        ["date"] = next.Date,
                        ["message_id"] = next.Message,
                        ["topic_id"] = next.Topic
                    } : null
                }.ToString(Formatting.None));
                break;
            case "jsonl":
                foreach (var row in rows)
                {
                    Console.WriteLine(row.ToString(Formatting.None));
                }
                break;
            case "tsv":
                Console.WriteLine("chat_id\ttopic_id\tname\tis_closed\tis_hidden\tis_pinned\tunread_count\tlast_message_id");
                foreach (var row in rows)
                {
                    Console.WriteLine(string.Join('\t',
                        row.Value<long>("chat_id"),
                        row.Value<int>("topic_id"),
                        Clean(row.Value<string>("name")),
                        row.Value<bool>("is_closed"),
                        row.Value<bool>("is_hidden"),
                        row.Value<bool>("is_pinned"),
                        row.Value<int>("unread_count"),
                        row.Value<long>("last_message_id")));
                }
                break;
            case "plain":
                foreach (var row in rows)
                {
                    Console.WriteLine($"{row.Value<int>("topic_id")}\t{Clean(row.Value<string>("name"))}");
                }
                break;
            default:
                throw new ArgumentException("Format must be one of: json, jsonl, tsv, plain.", nameof(format));
        }
    }

    internal static async Task<ForumTopicsResult> FetchTopicsAsync(
        long chatId, string query, int limit, bool all, int maxPages,
        Func<TdApi.GetForumTopics, Task<TdApi.ForumTopics>> fetch, CancellationToken cancellationToken)
    {
        ReadBounds.ValidatePage(0, 0, limit, maxPages);
        var topics = new List<TdApi.ForumTopic>();
        var seenTopics = new HashSet<int>();
        (int Date, long Message, int Topic) offset = default;
        var seenOffsets = new HashSet<(int Date, long Message, int Topic)> { offset };
        var budget = all ? maxPages : 1;

        for (var page = 1; page <= budget; page++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var response = await fetch(new TdApi.GetForumTopics
            {
                ChatId = chatId,
                Query = query,
                OffsetDate = offset.Date,
                OffsetMessageId = offset.Message,
                OffsetForumTopicId = offset.Topic,
                Limit = limit
            });
            cancellationToken.ThrowIfCancellationRequested();
            topics.AddRange((response.Topics ?? []).Where(topic => topic.Info is not null && seenTopics.Add(topic.Info.ForumTopicId)));
            offset = (response.NextOffsetDate, response.NextOffsetMessageId, response.NextOffsetForumTopicId);
            if (offset == default)
                return new(topics, null, true, "topics_exhausted", page);
            if (!seenOffsets.Add(offset))
                return new(topics, null, false, "cursor_not_advanced", page);
        }

        return new(topics, offset, false, all ? "max_pages_reached" : "page_limit", budget);
    }

    private static string Clean(string? value)
    {
        return (value ?? string.Empty).Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');
    }
}


internal sealed record ForumTopicsResult(List<TdApi.ForumTopic> Topics,
    (int Date, long Message, int Topic)? Next, bool Complete, string TerminationReason, int PagesFetched);
