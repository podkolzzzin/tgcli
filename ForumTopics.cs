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
        var mode = Output.ParseFormat(format);
        if (mode == OutputFormat.Md) throw new ArgumentException("Format must be one of: json, jsonl, tsv, plain.", nameof(format));
        ReadBounds.ValidatePage(0, 0, limit, maxPages);
        ForumTopicHistory.ValidateRequestTimeout(requestTimeout);
        await using var tg = await TelegramSession.CreateReadyAsync(session, lockTimeout, noWait,
            cancellationToken: cancellationToken);
        var result = await FetchTopicsAsync(chatId, query, limit, all, maxPages, request => tg.Client.ExecuteAsync(request),
            cancellationToken);
        var rows = result.Topics.Select(Wire.Topic).ToArray();
        var nextOffset = result.Next is { } next ? new JObject
        {
            ["date"] = next.Date,
            ["message_id"] = Wire.NullableId(next.Message == 0 ? null : next.Message),
            ["topic_id"] = Wire.NullableId(next.Topic == 0 ? null : next.Topic)
        } : null;
        var coverage = Wire.Coverage(Wire.Telegram, result.Complete, result.TerminationReason,
            new { chat_id = Wire.Id(chatId), query, limit, all }, nextOffset?.ToString(Formatting.None), result.PagesFetched, rows.Length);
        coverage["next_offset"] = nextOffset;
        coverage["limit_satisfied"] = rows.Length >= limit;
        tg.CheckActive();
        cancellationToken.ThrowIfCancellationRequested();

        if (mode == OutputFormat.Json) Output.WriteJsonCollection(Console.Out, new JArray(rows), coverage);
        else
        {
            string[] fields = ["chat_id", "topic_id", "name", "is_closed", "is_hidden", "is_pinned", "unread_count", "last_message_id"];
            if (mode == OutputFormat.Tsv) Console.WriteLine(string.Join('\t', fields));
            foreach (var row in rows)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Console.WriteLine(mode switch
                {
                    OutputFormat.Jsonl => row.ToString(Formatting.None),
                    OutputFormat.Tsv => string.Join('\t', fields.Select(name => Output.Clean((string?)row[name]))),
                    _ => $"{row.Value<string>("topic_id")}\t{Output.Clean(row.Value<string>("name"))}"
                });
            }
            Output.WriteSummary(coverage, cancellationToken);
        }
        if (!result.Complete && (all || rows.Length < limit))
            throw new PartialResultException("Forum topics are incomplete. See the acquisition coverage.");
    }

    internal static async Task<ForumTopicsResult> FetchTopicsAsync(
        long chatId, string query, int limit, bool all, int maxPages,
        Func<TdApi.GetForumTopics, Task<TdApi.ForumTopics>> fetch, CancellationToken cancellationToken,
        (int Date, long Message, int Topic) startOffset = default)
    {
        ReadBounds.ValidatePage(0, 0, limit, maxPages);
        var topics = new List<TdApi.ForumTopic>();
        var seenTopics = new HashSet<int>();
        var offset = startOffset;
        var seenOffsets = new HashSet<(int Date, long Message, int Topic)> { offset };

        for (var page = 1; page <= maxPages; page++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var response = await fetch(new TdApi.GetForumTopics
            {
                ChatId = chatId,
                Query = query,
                OffsetDate = offset.Date,
                OffsetMessageId = offset.Message,
                OffsetForumTopicId = offset.Topic,
                Limit = all ? limit : limit - topics.Count
            });
            cancellationToken.ThrowIfCancellationRequested();
            topics.AddRange((response.Topics ?? []).Where(topic => topic.Info is not null && seenTopics.Add(topic.Info.ForumTopicId)));
            var limitSatisfied = topics.Count >= limit;
            offset = (response.NextOffsetDate, response.NextOffsetMessageId, response.NextOffsetForumTopicId);
            if (offset == default)
                return new(topics, null, true, "topics_exhausted", page, limitSatisfied);
            if (!seenOffsets.Add(offset))
                return new(topics, null, false, "cursor_not_advanced", page, limitSatisfied);
            if (!all && limitSatisfied)
                return new(topics, offset, false, "page_limit", page, true);
        }

        return new(topics, offset, false, "max_pages_reached", maxPages, topics.Count >= limit);
    }

    private static string Clean(string? value)
    {
        return (value ?? string.Empty).Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');
    }
}


internal sealed record ForumTopicsResult(List<TdApi.ForumTopic> Topics,
    (int Date, long Message, int Topic)? Next, bool Complete, string TerminationReason, int PagesFetched, bool LimitSatisfied);
