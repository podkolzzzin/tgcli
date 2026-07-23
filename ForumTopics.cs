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
        CancellationToken cancellationToken)
    {
        ValidateTopicId(topicId);
        ValidateRequestTimeout(requestTimeout);

        return FetchAsync(
            chatId,
            topicId,
            fromMessageId,
            offset,
            limit,
            all,
            maxPages,
            async (request, token) =>
            {
                try
                {
                    return await tg.Client.ExecuteAsync(request)
                        .WaitAsync(TimeSpan.FromSeconds(requestTimeout), token);
                }
                catch (TimeoutException ex)
                {
                    throw new TimeoutException(
                        $"Telegram timed out after {requestTimeout} seconds while reading forum topic {topicId}.",
                        ex);
                }
            },
            cancellationToken);
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
        CancellationToken cancellationToken)
    {
        ValidateTopicId(topicId);
        var result = new ForumTopicHistoryResult();
        var seen = new HashSet<long>();
        var cursor = fromMessageId;
        var pageLimit = all ? PageSize : Math.Clamp(limit, 1, PageSize);
        var pageBudget = all ? Math.Max(1, maxPages) : 1;

        for (var page = 0; page < pageBudget; page++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var response = await fetchPage(new TdApi.GetForumTopicHistory
            {
                ChatId = chatId,
                ForumTopicId = topicId,
                FromMessageId = cursor,
                Offset = page == 0 ? offset : 0,
                Limit = pageLimit
            }, cancellationToken);
            result.PagesFetched++;

            var pageMessages = response.Messages_
                .Where(message => seen.Add(message.Id))
                .ToArray();
            if (pageMessages.Length == 0)
            {
                result.Complete = response.Messages_.Length == 0;
                result.TerminationReason = result.Complete ? "history_exhausted" : "cursor_stalled";
                break;
            }

            result.Messages.AddRange(pageMessages);
            var nextCursor = pageMessages.Min(message => message.Id);
            if (cursor != 0 && nextCursor >= cursor)
            {
                result.Complete = false;
                result.TerminationReason = "cursor_stalled";
                break;
            }

            cursor = nextCursor;
            if (!all || response.Messages_.Length < pageLimit)
            {
                result.Complete = true;
                result.TerminationReason = response.Messages_.Length < pageLimit
                    ? "short_page"
                    : "page_complete";
                break;
            }
        }

        if (!result.Complete && result.TerminationReason == "not_started")
        {
            result.TerminationReason = "max_pages_reached";
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
        if (requestTimeout <= 0)
        {
            throw new ValidationException("Request timeout must be greater than zero.");
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
        ForumTopicHistory.ValidateRequestTimeout(requestTimeout);
        await using var tg = await TelegramSession.CreateReadyAsync(session, lockTimeout, noWait);
        var topics = new List<TdApi.ForumTopic>();
        var seen = new HashSet<int>();
        var offsetDate = 0;
        long offsetMessageId = 0;
        var offsetTopicId = 0;
        var pageSize = Math.Clamp(limit, 1, 100);
        var pageBudget = all ? Math.Max(1, maxPages) : 1;

        for (var page = 0; page < pageBudget; page++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TdApi.ForumTopics response;
            try
            {
                response = await tg.Client.ExecuteAsync(new TdApi.GetForumTopics
                {
                    ChatId = chatId,
                    Query = query,
                    OffsetDate = offsetDate,
                    OffsetMessageId = offsetMessageId,
                    OffsetForumTopicId = offsetTopicId,
                    Limit = pageSize
                }).WaitAsync(TimeSpan.FromSeconds(requestTimeout), cancellationToken);
            }
            catch (TimeoutException ex)
            {
                throw new TimeoutException(
                    $"Telegram timed out after {requestTimeout} seconds while listing forum topics.",
                    ex);
            }

            var pageTopics = response.Topics
                .Where(topic => topic.Info is not null && seen.Add(topic.Info.ForumTopicId))
                .ToArray();
            topics.AddRange(pageTopics);
            if (pageTopics.Length == 0 && response.Topics.Length > 0)
            {
                throw new InvalidOperationException("Forum topic pagination cursor did not advance.");
            }

            if (!all || response.Topics.Length < pageSize || pageTopics.Length == 0)
            {
                break;
            }

            if (response.NextOffsetDate == offsetDate
                && response.NextOffsetMessageId == offsetMessageId
                && response.NextOffsetForumTopicId == offsetTopicId)
            {
                throw new InvalidOperationException("Forum topic pagination cursor did not advance.");
            }

            offsetDate = response.NextOffsetDate;
            offsetMessageId = response.NextOffsetMessageId;
            offsetTopicId = response.NextOffsetForumTopicId;
        }

        var rows = topics.Select(topic => new JObject
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

        switch (format.Trim().ToLowerInvariant())
        {
            case "json":
                Console.WriteLine(new JArray(rows).ToString(Formatting.None));
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

    private static string Clean(string? value)
    {
        return (value ?? string.Empty).Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');
    }
}
