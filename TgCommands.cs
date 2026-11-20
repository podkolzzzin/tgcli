using ConsoleAppFramework;
using System.Globalization;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using TdLib;

namespace TgCli;

public sealed class TgCommands
{
    /// <summary>
    /// Transcribe a local audio or video file using the embedded multilingual Whisper model.
    /// </summary>
    /// <param name="input">Local audio/video path (WAV, OGG, MP3, M4A, MP4 and other FFmpeg formats).</param>
    /// <param name="language">Speech language code, for example uk or en; auto detects the language.</param>
    /// <param name="format">Output format: json (with timestamped segments), jsonl or plain.</param>
    public async Task Transcribe(string input, string language = "auto", string format = "json", CancellationToken cancellationToken = default)
    {
        TranscriptionCommands.ValidateFormat(format);
        await using var transcription = TranscriptionCommands.Create(true, language)!;
        var transcript = await transcription.TranscribeFileAsync(input, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        TranscriptionCommands.Write(transcript, format);
    }

    /// <summary>
    /// Print machine-readable session, TDLib, account, database, and network diagnostics.
    /// </summary>
    public async Task Diagnostics(string format = "json", int lockTimeout = 5, bool noWait = false, string? session = null)
    {
        var outputFormat = Output.ParseFormat(format);
        if (outputFormat is not (OutputFormat.Json or OutputFormat.Jsonl or OutputFormat.Plain))
            throw new ArgumentException("Format must be one of: json, jsonl, plain.", nameof(format));
        if (lockTimeout < 0) throw new ArgumentException("Lock timeout must be zero or greater.", nameof(lockTimeout));

        var cancellationToken = CliExecution.CancellationToken;
        var remaining = CliExecution.Remaining;
        TimeSpan? probeBudget = remaining == Timeout.InfiniteTimeSpan ? null :
            TimeSpan.FromMilliseconds(Math.Max(0, remaining.TotalMilliseconds - 150));
        var result = await SessionDiagnostic.ProbeAsync(session, lockTimeout, noWait, cancellationToken, probeBudget);
        cancellationToken.ThrowIfCancellationRequested();
        Console.WriteLine(result.Data.ToString(Formatting.None));
        if (result.Error is not null) throw result.Error;
    }

    /// <summary>
    /// Sign in to Telegram and persist the local TDLib session.
    /// </summary>
    /// <param name="apiId">Telegram API id. Can also be set with TGCLI_API_ID.</param>
    /// <param name="apiHash">Telegram API hash. Can also be set with TGCLI_API_HASH.</param>
    /// <param name="phone">Phone number in international format. If omitted, tgcli prompts.</param>
    /// <param name="session">Session directory. Defaults to ~/.local/share/tgcli.</param>
    public async Task Login(int apiId = 0, string? apiHash = null, string? phone = null, string? session = null)
    {
        await using var tg = await TelegramSession.CreateAsync(session, apiId, apiHash, saveConfig: true,
            cancellationToken: CliExecution.CancellationToken);
        await tg.LoginAsync(phone, CliExecution.CancellationToken);
    }

    /// <summary>
    /// Search chats by title or username.
    /// </summary>
    /// <param name="query">Search text.</param>
    /// <param name="limit">Maximum number of chats to print.</param>
    /// <param name="local">Match known chats in the local cache only.</param>
    /// <param name="format">Output format: tsv, json, jsonl, plain, md.</param>
    /// <param name="session">Session directory. Defaults to ~/.local/share/tgcli.</param>
    public async Task Search(string query, int limit = 20, bool local = false, string format = "tsv", string? session = null,
        CancellationToken cancellationToken = default)
    {
        var mode = Output.ParseFormat(format);
        ReadBounds.ValidatePage(0, 0, limit, 1);
        if (string.IsNullOrWhiteSpace(query)) throw new ArgumentException("Search query must not be empty.", nameof(query));
        await using var tg = await TelegramSession.CreateReadyAsync(session, local: local, cancellationToken: cancellationToken);
        var chats = await TelegramOperations.SearchChatsAsync(tg, query, limit, server: !local,
            cancellationToken: cancellationToken, retainNativeOperation: true);
        var coverage = Wire.Coverage(local ? Wire.LocalCache : Wire.Telegram, false, "bounded_no_continuation",
            new { query, limit, chat_scope = "known_chats" }, rowsFetched: chats.ChatIds.Length);
        await Output.PrintChatsAsync(tg, chats.ChatIds, mode, coverage, cancellationToken, local);
    }

    /// <summary>
    /// Download a Telegram file by message, file id, or remote file id.
    /// </summary>
    /// <param name="type">File type: voice, attach/document, audio, video, photo, animation, video-note, thumbnail, file. Optional for --chat-id/--message-id.</param>
    /// <param name="attachmentId">TDLib integer file id printed by search/messages, or a remote file id string.</param>
    /// <param name="chatId">Chat id that contains the attachment.</param>
    /// <param name="messageId">Message id that contains the attachment.</param>
    /// <param name="output">Destination file or directory. Existing directories, paths ending with a separator, and missing extensionless paths are treated as directories. Defaults to the current directory.</param>
    /// <param name="session">Session directory. Defaults to ~/.local/share/tgcli.</param>
    public async Task Download(
        string? type = null,
        string? attachmentId = null,
        long chatId = 0,
        long messageId = 0,
        string output = ".",
        string? session = null,
        CancellationToken cancellationToken = default)
    {
        AttachmentDownloads.Validate(type, attachmentId, chatId, messageId);
        _ = Path.GetFullPath(output);
        cancellationToken.ThrowIfCancellationRequested();
        await using var tg = await TelegramSession.CreateReadyAsync(session, cancellationToken: cancellationToken);
        var selected = await AttachmentDownloads.SelectAsync(
            tg.Client, tg.SessionDirectory, type, attachmentId, chatId, messageId, cancellationToken);
        var file = await DownloadFileWithRefreshAsync(tg, selected.File, type, selected.ChatId, selected.MessageId, cancellationToken);

        var destination = ResolveDownloadDestination(output, file);
        await CopyDownloadedFileAsync(file, destination, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        Console.WriteLine(destination);
    }

    /// <summary>
    /// Download many message attachments concurrently through one TDLib client.
    /// </summary>
    /// <param name="input">JSONL with one record per message (chat_id, message_id and optional attachment_id), or - for stdin.</param>
    /// <param name="type">Attachment type applied to every row. If omitted, each row may provide type.</param>
    /// <param name="output">Destination directory. Defaults to the current directory.</param>
    /// <param name="parallel">Maximum concurrent downloads, from 1 to 32.</param>
    /// <param name="format">Output format: jsonl or plain.</param>
    /// <param name="session">Session directory. Defaults to ~/.local/share/tgcli.</param>
    public async Task DownloadBatch(
        string input,
        string? type = null,
        string output = ".",
        int parallel = 4,
        string format = "jsonl",
        string? session = null,
        CancellationToken cancellationToken = default)
    {
        var normalizedFormat = format.Trim().ToLowerInvariant();
        if (normalizedFormat is not ("jsonl" or "plain"))
        {
            throw new ArgumentException("Format must be one of: jsonl, plain.", nameof(format));
        }

        BatchDownloads.ValidateParallelism(parallel);
        IReadOnlyList<BatchDownloadRequest> requests;
        if (input == "-")
        {
            requests = BatchDownloads.Read(Console.In, type);
        }
        else
        {
            using var reader = File.OpenText(Path.GetFullPath(input));
            requests = BatchDownloads.Read(reader, type);
        }

        var outputDirectory = Path.GetFullPath(output);
        cancellationToken.ThrowIfCancellationRequested();
        await using var tg = await TelegramSession.CreateReadyAsync(session, cancellationToken: cancellationToken);
        var results = await BatchDownloads.RunAsync(requests, outputDirectory, parallel,
            (request, token) => ResolveDownloadFileAsync(tg, request.Type, request.AttachmentId, request.ChatId, request.MessageId, token),
            (request, file, token) => DownloadFileWithRefreshAsync(tg, file, request.Type, request.ChatId, request.MessageId, token),
            cancellationToken);

        foreach (var result in results)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Console.WriteLine(normalizedFormat == "jsonl"
                ? BatchDownloads.ToJson(result).ToString(Formatting.None)
                : result.Ok
                    ? $"{result.Status.ToUpperInvariant()}\t{result.Path}"
                    : $"{result.Status.ToUpperInvariant()}\t{result.ChatId}\t{result.MessageId}\t{result.Error}");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var failed = results.Count(x => !x.Ok);
        if (failed > 0)
        {
            throw new PartialResultException($"{failed} of {results.Count} batch downloads failed.");
        }
    }

    internal static Task<TdApi.File> ResolveDownloadFileAsync(
        TelegramSession tg, string? type, string? attachmentId, long chatId, long messageId,
        CancellationToken cancellationToken = default) =>
        ResolveDownloadFileAsync(tg.Client, tg.SessionDirectory, type, attachmentId, chatId, messageId, cancellationToken);

    internal static async Task<TdApi.File> ResolveDownloadFileAsync(
        TdApi.IClient client, string sessionDirectory, string? type, string? attachmentId, long chatId, long messageId,
        CancellationToken cancellationToken = default) =>
        (await AttachmentDownloads.SelectAsync(client, sessionDirectory, type, attachmentId, chatId, messageId, cancellationToken)).File;

    internal static Task<TdApi.File> DownloadFileWithRefreshAsync(
        TelegramSession tg, TdApi.File file, string? type, long chatId, long messageId,
        CancellationToken cancellationToken = default) =>
        DownloadFileWithRefreshAsync(tg.Client, file, type, chatId, messageId, cancellationToken);

    internal static Task<TdApi.File> DownloadFileWithRefreshAsync(
        TdApi.IClient client, TdApi.File file, string? type, long chatId, long messageId,
        CancellationToken cancellationToken = default) =>
        AttachmentDownloads.CompleteAsync(client, file, type, chatId, messageId, cancellationToken);

    internal static async Task CopyDownloadedFileAsync(TdApi.File file, string destination, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var source = AtomicDownload.GetCompletedPath(file);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (source.Equals(Path.GetFullPath(destination), comparison))
            return;

        using var stage = await AtomicDownload.StageAsync(file, destination, cancellationToken);
        stage.Promote(cancellationToken);
    }

    internal static string ResolveDownloadDestination(string output, TdLib.TdApi.File file)
    {
        var fullOutput = Path.GetFullPath(output);
        var looksLikeDirectory = output.EndsWith(Path.DirectorySeparatorChar)
            || output.EndsWith(Path.AltDirectorySeparatorChar)
            || Directory.Exists(fullOutput)
            || (!File.Exists(fullOutput) && string.IsNullOrWhiteSpace(Path.GetExtension(fullOutput)));

        if (!looksLikeDirectory)
        {
            return fullOutput;
        }

        var fileName = Path.GetFileName(file.Local?.Path);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            fileName = $"telegram-file-{file.Id}";
        }

        return Path.Combine(fullOutput, fileName);
    }
}

public sealed class LinkCommands
{
    /// <summary>
    /// Build Telegram links for a message.
    /// </summary>
    /// <param name="chatId">Chat id that contains the message.</param>
    /// <param name="messageId">Message id.</param>
    /// <param name="format">Output format: json, jsonl, tsv, plain.</param>
    /// <param name="session">Session directory. Defaults to ~/.local/share/tgcli.</param>
    public async Task Message(long chatId, long messageId, string format = "json", string? session = null, bool local = false,
        CancellationToken cancellationToken = default)
    {
        var mode = Output.ParseFormat(format);
        if (mode == OutputFormat.Md) throw new ArgumentException("Format must be one of: json, jsonl, tsv, plain.", nameof(format));
        ValidateIdentity(chatId, messageId);
        cancellationToken.ThrowIfCancellationRequested();
        var links = local ? Wire.LocalLink(chatId, messageId) : await FetchLinksAsync();
        var result = Wire.Link(links);
        result["source"] = local ? Wire.LocalCache : Wire.Telegram;
        cancellationToken.ThrowIfCancellationRequested();
        switch (mode)
        {
            case OutputFormat.Json or OutputFormat.Jsonl:
                Console.WriteLine(result.ToString(Formatting.None));
                break;
            case OutputFormat.Tsv:
                Console.WriteLine("chat_id\tmessage_id\tshort_message_id\ttg_url\ttg_url_scope\thttps_url\thttps_fallback");
                Console.WriteLine(string.Join('\t', result.Properties().Take(7).Select(property => Output.Clean((string?)property.Value))));
                break;
            case OutputFormat.Plain:
                Console.WriteLine(links.HttpsUrl ?? links.TgUrl ?? links.HttpsFallback);
                break;
        }
        if (links.TgUrl is null && links.HttpsUrl is null)
            throw new PartialResultException("No exact link is available for the requested message.");

        async Task<MessageLinkInfo> FetchLinksAsync()
        {
            await using var tg = await TelegramSession.CreateReadyAsync(session, cancellationToken: cancellationToken);
            var found = await MessageLinks.BuildAsync(tg, chatId, messageId, cancellationToken, retainNativeOperation: true);
            tg.CheckActive();
            return found;
        }
    }

    internal static void ValidateIdentity(long chatId, long messageId)
    {
        if (chatId == 0) throw new ArgumentException("Chat ID must be nonzero.", nameof(chatId));
        if (messageId <= 0) throw new ArgumentException("Message ID must be positive.", nameof(messageId));
    }
}

public sealed class MessageCommands
{
    /// <summary>
    /// Download and transcribe a Telegram voice note, video note, audio or video attachment locally.
    /// </summary>
    /// <param name="chatId">Chat id containing the media message.</param>
    /// <param name="messageId">TDLib message id.</param>
    /// <param name="language">Speech language code, for example uk or en; auto detects the language.</param>
    /// <param name="format">Output format: json, jsonl or plain.</param>
    /// <param name="session">Session directory. Defaults to ~/.local/share/tgcli.</param>
    public async Task Transcribe(long chatId, long messageId, string language = "auto", string format = "json", string? session = null, CancellationToken cancellationToken = default)
    {
        TranscriptionCommands.ValidateFormat(format);
        LinkCommands.ValidateIdentity(chatId, messageId);
        await using var transcription = TranscriptionCommands.Create(true, language)!;
        await using var tg = await TelegramSession.CreateReadyAsync(session, cancellationToken: cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var message = await tg.Client.GetMessageAsync(chatId, messageId);
        cancellationToken.ThrowIfCancellationRequested();
        var file = MediaTranscription.GetMedia(message);
        var path = await MediaTranscription.DownloadAsync(tg, file, cancellationToken, chatId: message.ChatId, messageId: message.Id);
        var transcript = await transcription.TranscribeFileAsync(path, cancellationToken);
        tg.CheckActive();
        cancellationToken.ThrowIfCancellationRequested();
        TranscriptionCommands.Write(transcript, format, chatId, messageId);
    }

    /// <summary>
    /// Get one full message as JSON, including extracted files and links.
    /// </summary>
    /// <param name="chatId">Chat id that contains the message.</param>
    /// <param name="messageId">Message id.</param>
    /// <param name="session">Session directory. Defaults to ~/.local/share/tgcli.</param>
    public async Task Get(long chatId, long messageId, string format = "json", string? session = null, bool local = false,
        CancellationToken cancellationToken = default)
    {
        var mode = Output.ParseFormat(format);
        if (mode == OutputFormat.Md) throw new ArgumentException("Format must be one of: json, jsonl, tsv, plain.", nameof(format));
        LinkCommands.ValidateIdentity(chatId, messageId);
        await using var tg = await TelegramSession.CreateReadyAsync(session, local: local, cancellationToken: cancellationToken);
        var message = await TelegramOperations.AwaitReadAsync(local ? tg.Client.GetMessageLocallyAsync(chatId, messageId)
            : tg.Client.GetMessageAsync(chatId, messageId), cancellationToken, retainNativeOperation: true);
        if (message.ChatId != chatId || message.Id != messageId)
            throw new InvalidDataException("TDLib returned a different message than requested.");
        var item = Output.ToExportedMessage(message);
        if (mode is OutputFormat.Json or OutputFormat.Jsonl)
        {
            var payload = await Output.BuildExportedRowAsync(tg, item, includeLinks: true, fields: null,
                transcription: null, cancellationToken, local);
            payload["source"] = local ? Wire.LocalCache : Wire.Telegram;
            Console.WriteLine(payload.ToString(Formatting.None));
        }
        else
        {
            await Output.WriteExportedMessagesAsync(Console.Out, tg, [item], mode,
                includeLinks: true, local: local, cancellationToken: cancellationToken);
        }
    }
}

public sealed class ChannelCommands
{
    /// <summary>
    /// Export per-post channel metrics and optional aggregates.
    /// </summary>
    public async Task Metrics(
        long chatId,
        long fromPostId = 0,
        long toPostId = 0,
        string? sinceDate = null,
        string? untilDate = null,
        string format = "jsonl",
        int maxPages = 1000,
        bool local = false,
        string? session = null,
        int requestTimeout = 60,
        CancellationToken cancellationToken = default)
    {
        var parsedFormat = format.Trim().ToLowerInvariant();
        if (parsedFormat is not ("jsonl" or "json" or "csv"))
            throw new ArgumentException("Format must be one of: jsonl, json, csv.", nameof(format));
        if (fromPostId is < 0 or > int.MaxValue || toPostId is < 0 or > int.MaxValue ||
            (toPostId > 0 && fromPostId > toPostId))
            throw new ArgumentException("Provide a valid public post ID range.");
        var (since, until) = ReadBounds.ParseDates(sinceDate, untilDate);
        ReadBounds.ValidatePage(0, 0, 100, maxPages);
        ForumTopicHistory.ValidateRequestTimeout(requestTimeout);
        cancellationToken.ThrowIfCancellationRequested();

        await using var tg = await TelegramSession.CreateReadyAsync(session, local: local);
        var history = await TelegramOperations.WithReadDeadlineAsync(requestTimeout, cancellationToken, false, async token =>
        {
            await ValidateChannelAsync(tg, chatId, token);
            return await ChatHistory.FetchAsync(tg, chatId, all: true, local, maxPages, followMigrations: false, token,
                requestTimeout: requestTimeout);
        });
        RequireKnownHistoryHead(history.TerminationReason);
        var rows = history.Messages.Select(x => x.Message)
            .Where(message => message.IsChannelPost && ChatHistory.TryGetPublicPostId(message.Id) is long postId
                && (fromPostId == 0 || postId >= fromPostId)
                && (toPostId == 0 || postId <= toPostId)
                && (since is null || DateTimeOffset.FromUnixTimeSeconds(message.Date) >= since)
                && (until is null || DateTimeOffset.FromUnixTimeSeconds(message.Date) <= until))
            .OrderBy(message => message.Id)
            .Select(ToMetricRow)
            .ToArray();
        var coverage = HistoryCoverage(history);

        switch (parsedFormat)
        {
            case "jsonl":
                foreach (var row in rows) Console.WriteLine(row.ToString(Formatting.None));
                Console.Error.WriteLine(coverage.ToString(Formatting.None));
                break;
            case "json":
                Console.WriteLine(new JObject
                {
                    ["chat_id"] = chatId,
                    ["post_count"] = rows.Length,
                    ["coverage"] = coverage,
                    ["aggregates"] = BuildMetricAggregates(rows),
                    ["posts"] = new JArray(rows)
                }.ToString(Formatting.None));
                break;
            case "csv":
                Console.WriteLine("post_id,message_id,date,kind,text_length,link_domains,view_count,forward_count,reply_count,reaction_count,paid_reaction_count,engagement_rate");
                foreach (var row in rows)
                {
                    Console.WriteLine(string.Join(',',
                        row.Value<long>("post_id"),
                        row.Value<long>("message_id"),
                        Csv(row.Value<string>("date")),
                        Csv(row.Value<string>("kind")),
                        row.Value<int>("text_length"),
                        Csv(string.Join(';', row["link_domains"]!.Values<string>())),
                        row.Value<long?>("view_count"),
                        row.Value<long?>("forward_count"),
                        row.Value<long?>("reply_count"),
                        row.Value<long?>("reaction_count"),
                        row.Value<long?>("paid_reaction_count"),
                        row.Value<double?>("engagement_rate")?.ToString("0.######", CultureInfo.InvariantCulture)));
                }
                Console.Error.WriteLine(coverage.ToString(Formatting.None));
                break;
        }
    }

    /// <summary>
    /// Export comments for one channel post or for all fetched channel posts.
    /// </summary>
    public async Task Comments(
        long chatId,
        long postId = 0,
        bool all = false,
        bool summary = false,
        string format = "jsonl",
        int maxPages = 1000,
        string? session = null,
        int requestTimeout = 60,
        CancellationToken cancellationToken = default)
    {
        var parsedFormat = format.Trim().ToLowerInvariant();
        if (parsedFormat is not ("json" or "jsonl"))
            throw new ArgumentException("Format must be one of: jsonl, json.", nameof(format));
        if ((all && postId != 0) || (!all && postId == 0))
            throw new ArgumentException("Provide exactly one of --post-id or --all.", nameof(postId));
        if (!all) _ = ChatHistory.ToChannelMessageId(postId);
        ReadBounds.ValidatePage(0, 0, 100, maxPages);
        ForumTopicHistory.ValidateRequestTimeout(requestTimeout);
        cancellationToken.ThrowIfCancellationRequested();

        await using var tg = await TelegramSession.CreateReadyAsync(session, cancellationToken: cancellationToken);
        var rows = await TelegramOperations.WithReadDeadlineAsync(requestTimeout, cancellationToken, false, async token =>
        {
            await ValidateChannelAsync(tg, chatId, token);
            var output = new List<JObject>();
            long[] postIds = [postId];
            if (all)
            {
                var history = await ChatHistory.FetchAsync(tg, chatId, all: true, local: false, maxPages,
                    followMigrations: false, token, requestTimeout: requestTimeout);
                RequireKnownHistoryHead(history.TerminationReason);
                postIds = history.Messages.Where(x => x.Message.IsChannelPost)
                    .Select(x => ChatHistory.TryGetPublicPostId(x.Message.Id)).OfType<long>().Distinct().Order().ToArray();
                var coverage = HistoryCoverage(history);
                coverage["record_type"] = "post_scan_coverage";
                coverage["channel_chat_id"] = chatId;
                output.Add(coverage);
            }

            foreach (var publicPostId in postIds)
            {
                token.ThrowIfCancellationRequested();
                var postRows = await FetchCommentRowsAsync(chatId, publicPostId, maxPages,
                    async request =>
                    {
                        try
                        {
                            return await tg.Client.ExecuteAsync(request).WaitAsync(token);
                        }
                        catch (TdException ex) when (TelegramOperations.IsMessageUnavailable(ex)
                            || ex.Error.Code == 400 && ex.Error.Message is "Message has no comments" or "Message has no thread")
                        {
                            return null;
                        }
                    },
                    request => tg.Client.ExecuteAsync(request).WaitAsync(token), token);
                if (summary) output.Add(BuildCommentSummary(postRows));
                else output.AddRange(postRows);
            }
            return output;
        });

        if (parsedFormat == "json")
            Console.WriteLine(new JArray(rows).ToString(Formatting.None));
        else
            foreach (var row in rows) Console.WriteLine(row.ToString(Formatting.None));
    }

    private static void RequireKnownHistoryHead(string reason)
    {
        if (reason == "newest_message_unavailable")
            throw new InvalidOperationException("TDLib did not return its known latest channel message (newest_message_unavailable).");
    }

    private static async Task ValidateChannelAsync(TelegramSession tg, long chatId, CancellationToken cancellationToken)
    {
        var chat = await tg.Client.GetChatAsync(chatId).WaitAsync(cancellationToken);
        if (chat.Type is not TdApi.ChatType.ChatTypeSupergroup { IsChannel: true })
            throw new ArgumentException("The requested chat must be a channel.", nameof(chatId));
    }

    private static JObject HistoryCoverage(HistoryFetchResult history) => new()
    {
        ["source"] = history.Local ? "cache" : "telegram",
        ["freshness"] = history.Local ? "cache" : "tdlib_update_state",
        ["complete"] = history.Complete,
        ["termination_reason"] = history.TerminationReason,
        ["pages_fetched"] = history.PagesFetched,
        ["next_message_id"] = history.NextMessageId
    };

    internal static JObject ToMetricRow(TdApi.Message message)
    {
        var metrics = ExportSchema.ToMetricsJson(message.InteractionInfo);
        var views = metrics.Value<long?>("view_count");
        var engagement = views is null ? (double?)null : views <= 0 ? 0
            : (metrics.Value<long>("forward_count") + metrics.Value<long>("reply_count")
                + metrics.Value<long>("reaction_count")) / (double)views.Value;
        var text = MessageFiles.GetMessageText(message.Content);
        return new JObject
        {
            ["post_id"] = ChatHistory.TryGetPublicPostId(message.Id),
            ["message_id"] = message.Id,
            ["chat_id"] = message.ChatId,
            ["date"] = DateTimeOffset.FromUnixTimeSeconds(message.Date).ToString("O", CultureInfo.InvariantCulture),
            ["kind"] = MessageFiles.GetKind(message.Content),
            ["text_length"] = text.Length,
            ["link_domains"] = new JArray(ExtractDomains(message.Content)),
            ["interaction_info_available"] = metrics["interaction_info_available"],
            ["view_count"] = metrics["view_count"],
            ["forward_count"] = metrics["forward_count"],
            ["reply_count"] = metrics["reply_count"],
            ["reaction_count"] = metrics["reaction_count"],
            ["reaction_counts"] = metrics["reaction_counts"],
            ["paid_reaction_count"] = metrics["paid_reaction_count"],
            ["has_comments"] = metrics["has_comments"],
            ["engagement_rate"] = engagement
        };
    }

    internal static JObject BuildMetricAggregates(IReadOnlyCollection<JObject> rows)
    {
        var known = rows.Where(x => x.Value<bool>("interaction_info_available")).ToArray();
        return new JObject
        {
            ["view_count"] = SumIfKnown(known, "view_count"),
            ["forward_count"] = SumIfKnown(known, "forward_count"),
            ["reply_count"] = SumIfKnown(known, "reply_count"),
            ["reaction_count"] = SumIfKnown(known, "reaction_count"),
            ["paid_reaction_count"] = SumIfKnown(known, "paid_reaction_count"),
            ["posts_with_comments"] = known.Length == 0 ? (long?)null : known.LongCount(x => x.Value<bool>("has_comments")),
            ["posts_with_unknown_interaction_info"] = rows.Count - known.Length,
            ["top_posts_by_views"] = new JArray(known.OrderByDescending(x => x.Value<long>("view_count")).Take(10).Select(x => new JObject
            {
                ["post_id"] = x["post_id"],
                ["view_count"] = x["view_count"]
            }))
        };
    }

    private static long? SumIfKnown(JObject[] rows, string key) => rows.Length == 0 ? null : rows.Sum(x => x.Value<long>(key));

    internal static async Task<List<JObject>> FetchCommentRowsAsync(long channelChatId, long publicPostId, int maxPages,
        Func<TdApi.GetMessageThread, Task<TdApi.MessageThreadInfo?>> fetchThread,
        Func<TdApi.GetMessageThreadHistory, Task<TdApi.Messages>> fetchHistory, CancellationToken cancellationToken)
    {
        var channelMessageId = ChatHistory.ToChannelMessageId(publicPostId);
        ReadBounds.ValidatePage(0, 0, 100, maxPages);
        cancellationToken.ThrowIfCancellationRequested();
        var thread = await fetchThread(new TdApi.GetMessageThread { ChatId = channelChatId, MessageId = channelMessageId });
        cancellationToken.ThrowIfCancellationRequested();
        var coverage = new JObject
        {
            ["record_type"] = "coverage",
            ["channel_chat_id"] = channelChatId,
            ["channel_post_id"] = publicPostId,
            ["channel_message_id"] = channelMessageId,
            ["complete"] = false,
            ["pages_fetched"] = 0,
            ["termination_reason"] = thread is null ? "thread_unavailable" : "max_pages_reached",
            ["is_inaccessible"] = thread is null
        };
        if (thread is null) return [coverage];
        coverage["discussion_chat_id"] = thread.ChatId;
        coverage["discussion_message_id"] = thread.MessageThreadId;

        var starters = (thread.Messages ?? []).DistinctBy(x => (x.ChatId, x.Id)).ToDictionary(x => (x.ChatId, x.Id));
        var messages = new Dictionary<(long, long), TdApi.Message>(starters);
        var cursor = 0L;
        var retriedNewest = false;
        for (var page = 0; page < maxPages; page++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var response = await fetchHistory(new TdApi.GetMessageThreadHistory
            {
                ChatId = thread.ChatId, MessageId = thread.MessageThreadId, FromMessageId = cursor, Offset = 0, Limit = 100
            });
            cancellationToken.ThrowIfCancellationRequested();
            coverage["pages_fetched"] = page + 1;
            var raw = response.Messages_ ?? [];
            if (cursor == 0 && thread.ReplyInfo?.LastMessageId is > 0 and var expectedLatest &&
                (raw.Length == 0 || raw.Max(message => message.Id) < expectedLatest))
            {
                coverage["termination_reason"] = "newest_reply_unavailable";
                if (!retriedNewest && page + 1 < maxPages)
                {
                    retriedNewest = true;
                    continue;
                }

                break;
            }
            if (raw.Length == 0)
            {
                coverage["complete"] = true;
                coverage["termination_reason"] = "history_exhausted";
                break;
            }

            var rows = ChatHistory.OlderHistoryRows(raw, cursor, cursor != 0);
            if (rows.Length == 0)
            {
                coverage["termination_reason"] = "cursor_not_advanced";
                break;
            }

            foreach (var row in rows) messages[(row.ChatId, row.Id)] = row;
            cursor = rows[^1].Id;
            coverage["termination_reason"] = "max_pages_reached";
        }

        var output = messages.Values.OrderBy(x => x.Date).ThenBy(x => x.ChatId).ThenBy(x => x.Id).Select(message => new JObject
        {
            ["record_type"] = "message",
            ["channel_chat_id"] = channelChatId,
            ["channel_post_id"] = publicPostId,
            ["channel_message_id"] = channelMessageId,
            ["discussion_chat_id"] = thread.ChatId,
            ["discussion_message_id"] = thread.MessageThreadId,
            ["comment_chat_id"] = message.ChatId,
            ["message_id"] = message.Id,
            ["date"] = DateTimeOffset.FromUnixTimeSeconds(message.Date).ToString("O", CultureInfo.InvariantCulture),
            ["sender"] = FormatSender(message.SenderId),
            ["text"] = MessageFiles.GetMessageText(message.Content),
            ["reply_to_message_id"] = (message.ReplyTo as TdApi.MessageReplyTo.MessageReplyToMessage)?.MessageId,
            ["reaction_counts"] = message.InteractionInfo is null ? JValue.CreateNull()
                : ExportSchema.BuildReactionCounts(message.InteractionInfo.Reactions?.Reactions ?? []),
            ["is_thread_root"] = (message.ChatId == thread.ChatId && message.Id == thread.MessageThreadId)
                || starters.ContainsKey((message.ChatId, message.Id)),
            ["is_inaccessible"] = false
        }).ToList();
        var observedReplies = output.Count(row => row.Value<bool>("is_thread_root") == false);
        coverage["observed_reply_count"] = observedReplies;
        if (coverage.Value<bool>("complete") && thread.ReplyInfo?.ReplyCount > observedReplies)
        {
            coverage["complete"] = false;
            coverage["termination_reason"] = "replies_missing_from_history";
        }
        if (cursor != 0 && !coverage.Value<bool>("complete"))
            coverage["next_message_id"] = cursor;
        output.Add(coverage);
        return output;
    }

    private static JObject BuildCommentSummary(IReadOnlyList<JObject> rows)
    {
        var coverage = rows[^1];
        var comments = rows.Where(x => x.Value<string>("record_type") == "message" && x.Value<bool?>("is_thread_root") != true).ToArray();
        var dates = comments.Select(x => x.Value<string>("date")).Order(StringComparer.Ordinal).ToArray();
        var senders = comments.Select(x => x.Value<string>("sender"))
            .Where(x => !string.IsNullOrWhiteSpace(x)).GroupBy(x => x!).ToArray();
        return new JObject
        {
            ["record_type"] = "summary",
            ["channel_post_id"] = coverage["channel_post_id"],
            ["channel_chat_id"] = coverage["channel_chat_id"],
            ["channel_message_id"] = coverage["channel_message_id"],
            ["discussion_chat_id"] = coverage["discussion_chat_id"],
            ["discussion_message_id"] = coverage["discussion_message_id"],
            ["comment_count"] = comments.Length,
            ["unique_commenters"] = senders.Length,
            ["top_commenters"] = new JArray(senders.OrderByDescending(x => x.Count()).ThenBy(x => x.Key, StringComparer.Ordinal).Take(10)
                .Select(x => new JObject { ["sender"] = x.Key, ["count"] = x.Count() })),
            ["first_comment_date"] = dates.FirstOrDefault(),
            ["last_comment_date"] = dates.LastOrDefault(),
            ["is_inaccessible"] = coverage["is_inaccessible"],
            ["complete"] = coverage["complete"],
            ["termination_reason"] = coverage["termination_reason"],
            ["pages_fetched"] = coverage["pages_fetched"]
        };
    }

    private static IEnumerable<string> ExtractDomains(TdApi.MessageContent? content)
    {
        var formatted = content switch
        {
            TdApi.MessageContent.MessageText item => item.Text,
            TdApi.MessageContent.MessageVoiceNote item => item.Caption,
            TdApi.MessageContent.MessageAudio item => item.Caption,
            TdApi.MessageContent.MessageDocument item => item.Caption,
            TdApi.MessageContent.MessagePhoto item => item.Caption,
            TdApi.MessageContent.MessageVideo item => item.Caption,
            TdApi.MessageContent.MessageAnimation item => item.Caption,
            TdApi.MessageContent.MessagePaidMedia item => item.Caption,
            _ => null
        };
        if (formatted is null) return [];
        var visibleUrls = System.Text.RegularExpressions.Regex.Matches(formatted.Text ?? "", @"https?://[^\s<>)]+", System.Text.RegularExpressions.RegexOptions.IgnoreCase)
            .Select(match => match.Value.TrimEnd('.', ',', ';', '!', '?'));
        var text = formatted.Text ?? "";
        var entityUrls = (formatted.Entities ?? []).Select(entity => entity.Type switch
        {
            TdApi.TextEntityType.TextEntityTypeTextUrl textUrl => textUrl.Url,
            TdApi.TextEntityType.TextEntityTypeUrl when entity.Offset >= 0 && entity.Length > 0
                && entity.Offset <= text.Length && entity.Length <= text.Length - entity.Offset =>
                    WithScheme(text.Substring(entity.Offset, entity.Length)),
            _ => null
        }).OfType<string>();
        return visibleUrls.Concat(entityUrls).Select(raw =>
            Uri.TryCreate(raw, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http" ? uri.IdnHost.ToLowerInvariant() : null)
            .OfType<string>().Where(host => host.Length > 0).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);
    }

    private static string WithScheme(string url) =>
        url.Contains("://", StringComparison.Ordinal) ? url : "https://" + url;

    private static string FormatSender(TdApi.MessageSender? sender) => sender switch
    {
        TdApi.MessageSender.MessageSenderUser user => $"user:{user.UserId}",
        TdApi.MessageSender.MessageSenderChat chat => $"chat:{chat.ChatId}",
        _ => string.Empty
    };

    private static string Csv(string? value) => "\"" + (value ?? string.Empty).Replace("\"", "\"\"") + "\"";
}

public sealed class ChatCommands
{
    /// <summary>
    /// List chats from the main chat list.
    /// </summary>
    /// <param name="limit">Maximum number of chats to print.</param>
    /// <param name="format">Output format: tsv, json, jsonl, plain, md.</param>
    /// <param name="session">Session directory. Defaults to ~/.local/share/tgcli.</param>
    public async Task List(int limit = 50, string format = "tsv", string? session = null, CancellationToken cancellationToken = default)
    {
        var mode = Output.ParseFormat(format);
        ReadBounds.ValidatePage(0, 0, limit, 1);
        await using var tg = await TelegramSession.CreateReadyAsync(session, cancellationToken: cancellationToken);
        var chats = await TelegramOperations.ListChatsAsync(tg, limit, cancellationToken, retainNativeOperation: true);
        var complete = chats.ChatIds.Length < limit;
        var coverage = Wire.Coverage(Wire.Telegram, complete, complete ? "chat_list_exhausted" : "page_limit",
            new { limit, chat_scope = "main" }, rowsFetched: chats.ChatIds.Length);
        await Output.PrintChatsAsync(tg, chats.ChatIds, mode, coverage, cancellationToken);
    }

    /// <summary>
    /// Resolve a public username to a chat.
    /// </summary>
    /// <param name="username">Telegram username, with or without @.</param>
    /// <param name="format">Output format: tsv, json, jsonl, plain, md.</param>
    /// <param name="session">Session directory. Defaults to ~/.local/share/tgcli.</param>
    public async Task Resolve(string username, string format = "tsv", string? session = null, CancellationToken cancellationToken = default)
    {
        var mode = Output.ParseFormat(format);
        username = TelegramOperations.NormalizeUsername(username);
        cancellationToken.ThrowIfCancellationRequested();
        await using var tg = await TelegramSession.CreateReadyAsync(session, cancellationToken: cancellationToken);
        var chat = await TelegramOperations.ResolveChatAsync(tg, username, cancellationToken, retainNativeOperation: true);
        await Output.PrintChatAsync(tg, chat, mode, cancellationToken);
    }

    /// <summary>
    /// Print messages from a chat.
    /// </summary>
    /// <param name="chatId">Chat id from tgcli chat list/search/resolve.</param>
    /// <param name="topicId">Forum topic id. Use tgcli forum topics to list ids.</param>
    /// <param name="limit">Maximum matching messages unless --all is used.</param>
    /// <param name="fromMessageId">Start before this TDLib message ID with offset 0. Use the reported continuation ID.</param>
    /// <param name="offset">TDLib history offset, usually 0.</param>
    /// <param name="local">Read local database only.</param>
    /// <param name="all">Fetch all pages until history ends or max-pages is reached.</param>
    /// <param name="maxPages">Maximum number of Telegram page requests.</param>
    /// <param name="format">Output format: tsv, json, jsonl, plain, md.</param>
    /// <param name="session">Session directory. Defaults to ~/.local/share/tgcli.</param>
    public async Task Messages(
        long chatId,
        int topicId = 0,
        int limit = 30,
        long fromMessageId = 0,
        int offset = 0,
        bool local = false,
        bool all = false,
        int maxPages = 1000,
        bool serviceOnly = false,
        string? kind = null,
        string format = "tsv",
        int requestTimeout = 60,
        int lockTimeout = 30,
        bool noWait = false,
        string? session = null,
        CancellationToken cancellationToken = default)
    {
        ValidateTopicScope(topicId, local);
        ReadBounds.ValidatePage(fromMessageId, offset, limit, maxPages);
        ForumTopicHistory.ValidateRequestTimeout(requestTimeout);
        var mode = Output.ParseFormat(format);
        var matches = MessagePredicate(serviceOnly, kind);
        await using var tg = await TelegramSession.CreateReadyAsync(session, lockTimeout, noWait, local, cancellationToken);
        var topicHistory = topicId > 0
            ? await ForumTopicHistory.FetchAsync(tg, chatId, topicId, fromMessageId, offset, limit, all, maxPages,
                requestTimeout, cancellationToken, retainNativeOperation: true, match: matches)
            : null;
        var history = topicHistory is null
            ? await ChatHistory.FetchAsync(tg, chatId, all, local, maxPages, followMigrations: false, cancellationToken,
                fromMessageId, offset, limit, matches, requestTimeout, retainNativeOperation: true)
            : null;
        var messages = topicHistory?.Messages ?? history!.Messages.Select(x => x.Message).ToList();
        var complete = topicHistory?.Complete ?? history!.Complete;
        var satisfied = topicHistory?.LimitSatisfied ?? history!.LimitSatisfied;
        var reason = topicHistory?.TerminationReason ?? history!.TerminationReason;
        var pages = topicHistory?.PagesFetched ?? history!.PagesFetched;
        var next = topicHistory is not null ? topicHistory.NextMessageId : history!.NextMessageId;
        var scope = new { chat_id = Wire.Id(chatId), topic_id = topicId == 0 ? null : Wire.Id(topicId), all, limit,
            start_message_id = fromMessageId == 0 ? null : Wire.Id(fromMessageId), start_offset = offset, service_only = serviceOnly, kind };
        var coverage = ReadCoverage(complete, satisfied, reason, pages, next, local, scope, messages.Count);
        coverage["freshness"] = local ? "cache" : topicId > 0 ? "topic_history" : "tdlib_update_state";
        await Output.WriteReadMessagesAsync(tg, messages, mode, coverage, cancellationToken, local,
            includeLinks: mode is OutputFormat.Json or OutputFormat.Jsonl);
        if (PageIncomplete(all, complete, satisfied, reason))
            throw new PartialResultException($"History is incomplete after {pages} pages ({reason}).");
    }

    /// <summary>
    /// Inspect the complete basic-group to supergroup migration chain.
    /// </summary>
    public async Task Migrations(long chatId, string format = "json", string? session = null, CancellationToken cancellationToken = default)
    {
        var parsedFormat = format.Trim().ToLowerInvariant();
        if (parsedFormat is not ("json" or "jsonl" or "tsv" or "plain"))
            throw new ArgumentException("Format must be one of: json, jsonl, tsv, plain.", nameof(format));
        cancellationToken.ThrowIfCancellationRequested();
        await using var tg = await TelegramSession.CreateReadyAsync(session, cancellationToken: cancellationToken);
        var chain = await ChatHistory.GetMigrationChainAsync(tg, chatId, cancellationToken, retainNativeOperation: true);
        tg.CheckActive();
        cancellationToken.ThrowIfCancellationRequested();
        switch (parsedFormat)
        {
            case "json":
            case "jsonl":
                Console.WriteLine(JsonConvert.SerializeObject(new { chat_id = Wire.Id(chatId), chain = chain.Select(Wire.Id) }, Formatting.None));
                break;
            case "tsv":
                Console.WriteLine("position\tchat_id");
                for (var i = 0; i < chain.Count; i++) Console.WriteLine($"{i}\t{chain[i]}");
                break;
            case "plain":
                Console.WriteLine(string.Join(Environment.NewLine, chain));
                break;
            default:
                throw new ArgumentException("Format must be one of: json, jsonl, tsv, plain.", nameof(format));
        }
    }

    /// <summary>
    /// Search messages inside a chat.
    /// </summary>
    /// <param name="chatId">Chat id from tgcli chat list/search/resolve.</param>
    /// <param name="topicId">Forum topic id. Use tgcli forum topics to list ids.</param>
    /// <param name="query">Search text. Use an empty string with --type to search attachments.</param>
    /// <param name="queries">File with one search query per line.</param>
    /// <param name="type">Filter: all, voice, attach/document, audio, video, photo, animation, video-note.</param>
    /// <param name="limit">Maximum matching messages unless --all is used.</param>
    /// <param name="fromMessageId">Start before this TDLib message ID with offset 0. Use the reported continuation ID.</param>
    /// <param name="offset">TDLib search offset, usually 0.</param>
    /// <param name="sinceDate">First included UTC date or ISO 8601 timestamp.</param>
    /// <param name="untilDate">Last included UTC date or ISO 8601 timestamp.</param>
    /// <param name="all">Fetch all pages until search results end or max-pages is reached.</param>
    /// <param name="maxPages">Maximum number of Telegram page requests.</param>
    /// <param name="format">Output format: tsv, json, jsonl, plain, md.</param>
    /// <param name="session">Session directory. Defaults to ~/.local/share/tgcli.</param>
    public async Task Search(
        long chatId,
        int topicId = 0,
        string query = "",
        string? queries = null,
        string type = "all",
        int limit = 30,
        long fromMessageId = 0,
        int offset = 0,
        bool all = false,
        int maxPages = 1000,
        string format = "tsv",
        bool serviceOnly = false,
        string? kind = null,
        int requestTimeout = 60,
        int lockTimeout = 30,
        bool noWait = false,
        string? session = null,
        CancellationToken cancellationToken = default,
        string? sinceDate = null,
        string? untilDate = null)
    {
        ValidateTopicScope(topicId, false);
        ReadBounds.ValidatePage(fromMessageId, offset, limit, maxPages, search: true);
        ForumTopicHistory.ValidateRequestTimeout(requestTimeout);
        var parsedFormat = Output.ParseFormat(format);
        var searchQueries = LoadQueries(query, queries);
        var nativeFilter = AttachmentKinds.ToSearchFilter(type);
        var filesOnly = AttachmentKinds.Parse(type) == AttachmentKind.File;
        var (since, until) = ReadBounds.ParseDates(sinceDate, untilDate);
        var matches = MessagePredicate(serviceOnly, kind);
        bool InScope(TdApi.Message message)
        {
            if (!matches(message) || (filesOnly && MessageFiles.GetAllFiles(message).Count == 0))
                return false;
            var date = DateTimeOffset.FromUnixTimeSeconds(message.Date);
            return (since is null || date >= since) && (until is null || date <= until);
        }

        await using var tg = await TelegramSession.CreateReadyAsync(session, lockTimeout, noWait,
            cancellationToken: cancellationToken);
        var reports = new JArray();
        var groups = new List<(ExportedMessage Item, string? Query)>();
        var incomplete = false;
        foreach (var searchQuery in searchQueries)
        {
            var result = await TelegramOperations.SearchMessagePagesAsync(fromMessageId, offset, limit, all, maxPages,
                (cursor, pageOffset, pageSize) => tg.Client.SearchChatMessagesAsync(chatId,
                    CreateMessageTopic(topicId), searchQuery, senderId: null!, cursor, pageOffset, pageSize, nativeFilter),
                InScope, cancellationToken);
            var scope = new { chat_id = Wire.Id(chatId), topic_id = topicId == 0 ? null : Wire.Id(topicId), all, limit,
                start_message_id = fromMessageId == 0 ? null : Wire.Id(fromMessageId), start_offset = offset, type,
                since_date = since is null ? null : Wire.Timestamp(since.Value), until_date = until is null ? null : Wire.Timestamp(until.Value),
                service_only = serviceOnly, kind };
            var report = ReadCoverage(result.Complete, result.LimitSatisfied, result.TerminationReason, result.PagesFetched,
                result.NextMessageId, false, scope, result.Messages.Count);
            report["query"] = searchQuery;
            incomplete |= PageIncomplete(all, result.Complete, result.LimitSatisfied, result.TerminationReason);
            var rows = result.Messages.Select(message => (Output.ToExportedMessage(message), (string?)searchQuery));
            if (parsedFormat == OutputFormat.Json)
            {
                using var writer = new StringWriter(CultureInfo.InvariantCulture);
                await Output.WriteMessageGroupsAsync(writer, tg, rows, parsedFormat, coverage: report,
                    cancellationToken: cancellationToken);
            }
            else groups.AddRange(rows);
            reports.Add(report);
        }

        tg.CheckActive();
        cancellationToken.ThrowIfCancellationRequested();
        var document = BuildSearchDocument(reports);
        if (parsedFormat == OutputFormat.Json)
            Console.WriteLine(document.ToString(Formatting.None));
        else
        {
            await Output.WriteMessageGroupsAsync(Console.Out, tg, groups, parsedFormat,
                includeLinks: parsedFormat == OutputFormat.Jsonl, cancellationToken: cancellationToken);
            Output.WriteSummary(document, cancellationToken);
        }

        if (incomplete) throw new PartialResultException("Search is incomplete. See the query coverage.");
    }

    internal static JObject BuildSearchDocument(JArray reports) => new()
    {
        ["source"] = Wire.Telegram,
        ["queries"] = reports,
        ["complete"] = reports.All(report => report.Value<bool>("complete"))
    };

    /// <summary>
    /// Export chat history with coverage and snapshot metadata.
    /// </summary>
    /// <param name="chatId">Chat id from tgcli chat list/search/resolve.</param>
    /// <param name="output">Destination file, or - for stdout.</param>
    /// <param name="format">Output format (md, jsonl, json, tsv, or plain).</param>
    /// <param name="limit">Bound the read to 1–100 messages. Omit or use 0 to export the entire history including migrations.</param>
    /// <param name="maxPages">Maximum history pages to fetch per source chat.</param>
    /// <param name="local">Read a fresh view of the local cache with unverified server coverage.</param>
    /// <param name="includeLinks">Include tg:// and https links for each exported message.</param>
    /// <param name="resume">Upsert a matching JSONL snapshot and retain messages absent from this scan.</param>
    /// <param name="incremental">Refresh a matching JSONL snapshot, including verified missing messages.</param>
    /// <param name="sinceMessageId">Export higher message IDs in the requested chat. Migration segments have separate IDs.</param>
    /// <param name="sinceDate">Include messages dated at or after this timestamp.</param>
    /// <param name="resumeToken">Use the per-chat checkpoint from a matching JSONL manifest.</param>
    /// <param name="fields">Project a comma-separated set of JSON or JSONL fields.</param>
    /// <param name="session">Session directory. Defaults to ~/.local/share/tgcli.</param>
    /// <param name="transcribe">Transcribe voice notes, video notes, audio and video with the embedded model.</param>
    /// <param name="transcribeLanguage">Speech language code, or auto.</param>
    /// <param name="transcribeCommand">Optional external transcription executable, mutually exclusive with --transcribe.</param>
    /// <param name="transcribeCommandArgs">JSON array of separate arguments for the external transcription executable.</param>
    /// <param name="expectSince">Require at least one fetched message at or before this timestamp.</param>
    /// <param name="expectCountMin">Require at least this many fetched messages in scope.</param>
    public async Task Export(
        long chatId,
        string output = "-",
        string format = "md",
        int limit = 0,
        int maxPages = 1000,
        bool local = false,
        bool includeLinks = false,
        bool resume = false,
        bool incremental = false,
        long sinceMessageId = 0,
        string? sinceDate = null,
        string? resumeToken = null,
        string? fields = null,
        string? transcribeCommand = null,
        bool transcribe = false,
        string transcribeLanguage = "auto",
        string? expectSince = null,
        int expectCountMin = 0,
        string? session = null,
        CancellationToken cancellationToken = default,
        string? transcribeCommandArgs = null)
    {
        var arguments = TranscriptionCommands.ParseArguments(transcribeCommandArgs);
        var request = ExportRequest.Create(chatId, output, format, local: local, maxPages: maxPages, includeLinks: includeLinks,
            resume: resume, incremental: incremental, sinceMessageId: sinceMessageId, sinceDate: sinceDate, resumeToken: resumeToken,
            fields: fields, transcribe: transcribe, transcribeLanguage: transcribeLanguage, transcribeCommand: transcribeCommand,
            expectSince: expectSince, expectCountMin: expectCountMin, limit: limit, transcribeCommandArguments: arguments);
        cancellationToken.ThrowIfCancellationRequested();
        await using var transcription = TranscriptionCommands.Create(transcribe, transcribeLanguage, transcribeCommand, arguments);
        await using var tg = await TelegramSession.CreateReadyAsync(session, local: local, cancellationToken: cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var principal = await tg.Client.GetMeAsync();
        cancellationToken.ThrowIfCancellationRequested();
        request = request.BindPrincipal(principal.Id);
        using var store = request.Path is null ? null : ExportSnapshotStore.Open(request.Path);
        var previous = request.UpdatesSnapshot ? store!.ReadCache(request, cancellationToken) : null;

        var history = await ChatHistory.FetchAsync(tg, chatId, request.All, local, maxPages,
            followMigrations: request.All && !local, cancellationToken: cancellationToken,
            limit: request.Limit == 0 ? 100 : request.Limit, retainNativeOperation: true);
        var visibility = history.Complete && request.Incremental && previous is not null
            ? await ExportHistoryVisibility.ReadAsync(tg, previous.Sources, cancellationToken)
            : null;
        var snapshot = ExportSnapshot.Build(request, history, previous, visibility);
        var title = request.Format == OutputFormat.Md
            ? local ? $"Chat {Wire.Id(chatId)}" : (await TelegramOperations.AwaitReadAsync(tg.Client.GetChatAsync(chatId),
                cancellationToken, retainNativeOperation: true)).Title
            : null;
        tg.CheckActive();
        cancellationToken.ThrowIfCancellationRequested();

        Task WriteAsync(TextWriter writer) => request.HasRowIdentity
            ? snapshot.WriteAsync(writer, row => Output.BuildExportedRowAsync(tg, row, includeLinks,
                request.Fields, transcription, cancellationToken, local), cancellationToken)
            : Output.WriteExportedMessagesAsync(writer, tg, snapshot.Messages, request.Format, title,
                includeLinks, request.Fields, transcription, cancellationToken, local,
                coverage: request.Path is null && request.Format == OutputFormat.Json ? (JObject)snapshot.Manifest.DeepClone() : null);

        if (store is null)
        {
            await WriteAsync(Console.Out);
            await Console.Out.FlushAsync(cancellationToken);
            if (request.Format != OutputFormat.Json) Output.WriteSummary(snapshot.Manifest, cancellationToken);
        }
        else
        {
            var manifest = await store.CommitAsync(snapshot.Manifest, WriteAsync, cancellationToken);
            Output.WriteSummary(manifest, cancellationToken);
        }
    }

    /// <summary>
    /// Return messages around a target and optionally follow its reply chain.
    /// </summary>
    public async Task Context(
        long chatId,
        long messageId,
        int before = 5,
        int after = 5,
        bool followReplyChain = true,
        string format = "jsonl",
        string? session = null,
        int requestTimeout = 60,
        CancellationToken cancellationToken = default)
    {
        ReadBounds.ValidateContext(messageId, before, after);
        var parsedFormat = Output.ParseFormat(format);
        ForumTopicHistory.ValidateRequestTimeout(requestTimeout);
        cancellationToken.ThrowIfCancellationRequested();
        await using var tg = await TelegramSession.CreateReadyAsync(session, cancellationToken: cancellationToken);
        var result = await TelegramOperations.ContextDetailsAsync(tg, chatId, messageId, before, after, followReplyChain,
            cancellationToken, retainNativeOperation: true);
        var coverage = Wire.Coverage(Wire.Telegram, result.WindowComplete,
            result.WindowComplete ? "context_window_returned" : "context_window_partial",
            new { chat_id = Wire.Id(chatId), message_id = Wire.Id(messageId), before, after, follow_reply_chain = followReplyChain },
            rowsFetched: result.Messages.Count);
        coverage["context"] = Wire.ContextCoverage(result);
        await Output.WriteReadMessagesAsync(tg, result.Messages, parsedFormat, coverage, cancellationToken);
        if (!result.WindowComplete || result.UnavailableAncestor is not null)
            throw new PartialResultException("Context is incomplete. See the window and reply-chain coverage.");
    }

    /// <summary>
    /// Print chat boundary and aggregate statistics.
    /// </summary>
    public async Task Stats(
        long chatId,
        int topicId = 0,
        string type = "all",
        int maxPages = 1000,
        bool local = false,
        string format = "json",
        int requestTimeout = 60,
        int lockTimeout = 30,
        bool noWait = false,
        string? session = null,
        CancellationToken cancellationToken = default)
    {
        var parsedFormat = format.Trim().ToLowerInvariant();
        if (parsedFormat is not ("json" or "jsonl" or "tsv" or "plain"))
            throw new ArgumentException("Format must be one of: json, jsonl, tsv, plain.", nameof(format));
        ValidateTopicScope(topicId, local);
        _ = AttachmentKinds.Parse(type);
        ReadBounds.ValidatePage(0, 0, 100, maxPages);
        ForumTopicHistory.ValidateRequestTimeout(requestTimeout);
        cancellationToken.ThrowIfCancellationRequested();
        await using var tg = await TelegramSession.CreateReadyAsync(session, lockTimeout, noWait, local, cancellationToken);
        var payload = Wire.Stats(await TelegramOperations.StatsAsync(tg, chatId, topicId, type, maxPages, local, requestTimeout,
            cancellationToken, retainNativeOperation: true));
        tg.CheckActive();
        cancellationToken.ThrowIfCancellationRequested();
        Console.WriteLine(parsedFormat switch
        {
            "json" or "jsonl" => payload.ToString(Formatting.None),
            "plain" => payload.ToString(Formatting.None),
            "tsv" => $"chat_id\tcount\tcount_kind\tfirst_timestamp\tlast_timestamp\tparticipant_count\tattachments\n{payload["chat_id"]}\t{payload["count"]}\t{payload["count_kind"]}\t{payload["first_timestamp"]}\t{payload["last_timestamp"]}\t{payload["participant_count"]}\t{payload["attachments"]?["count"]}",
            _ => throw new ArgumentException("Format must be one of: json, jsonl, tsv, plain.", nameof(format))
        });
        if (!payload.Value<bool>("complete") && (!local || payload.Value<string>("termination_reason") != "local_cache_exhausted"))
            throw new PartialResultException("Statistics are incomplete. See the acquisition coverage.");
    }

    private static JObject ReadCoverage(bool complete, bool satisfied, string reason, int pages, long? next, bool local,
        object scope, int count)
    {
        var continuation = next is long value ? Wire.Id(value) : null;
        var coverage = Wire.Coverage(local ? Wire.LocalCache : Wire.Telegram, complete, reason, scope,
            continuation, pages, count);
        coverage["limit_satisfied"] = satisfied;
        coverage["next_message_id"] = continuation;
        return coverage;
    }

    private static bool PageIncomplete(bool all, bool complete, bool satisfied, string reason) =>
        !complete && (all || !satisfied && reason != "local_cache_exhausted");

    private static void ValidateTopicScope(int topicId, bool local)
    {
        if (topicId < 0)
            throw new ArgumentException("Topic ID must be zero or greater.", nameof(topicId));
        if (topicId > 0 && local)
            throw new ArgumentException("Cache reads are unavailable for forum topics.", nameof(local));
    }

    private static TdApi.MessageTopic CreateMessageTopic(int topicId)
    {
        return topicId > 0
            ? new TdApi.MessageTopic.MessageTopicForum { ForumTopicId = topicId }
            : null!;
    }

    private static string[] LoadQueries(string query, string? queries)
    {
        if (string.IsNullOrWhiteSpace(queries))
        {
            return [query];
        }

        if (!string.IsNullOrWhiteSpace(query))
            throw new ArgumentException("Provide either a query or a query file.");
        var values = File.ReadLines(queries)
            .Select(x => x.Trim())
            .Where(x => x.Length > 0 && !x.StartsWith('#'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return values.Length > 0 ? values : throw new ArgumentException("The query file has no search terms.");
    }

    internal static IEnumerable<TdApi.Message> FilterMessages(IEnumerable<TdApi.Message> messages, bool serviceOnly, string? kind) =>
        messages.Where(MessagePredicate(serviceOnly, kind));

    internal static Func<TdApi.Message, bool> MessagePredicate(bool serviceOnly, string? kind)
    {
        var requestedKind = string.IsNullOrWhiteSpace(kind) ? null : NormalizeKind(kind);
        return message =>
            (!serviceOnly || IsServiceMessage(message.Content))
            && (requestedKind is null || NormalizeKind(MessageFiles.GetKind(message.Content)) == requestedKind);
    }

    internal static bool IsServiceMessage(TdApi.MessageContent? content) => content is
        TdApi.MessageContent.MessageBasicGroupChatCreate
        or TdApi.MessageContent.MessageBotWriteAccessAllowed
        or TdApi.MessageContent.MessageCall
        or TdApi.MessageContent.MessageChatAddMembers
        or TdApi.MessageContent.MessageChatBoost
        or TdApi.MessageContent.MessageChatChangePhoto
        or TdApi.MessageContent.MessageChatChangeTitle
        or TdApi.MessageContent.MessageChatDeleteMember
        or TdApi.MessageContent.MessageChatDeletePhoto
        or TdApi.MessageContent.MessageChatHasProtectedContentDisableRequested
        or TdApi.MessageContent.MessageChatHasProtectedContentToggled
        or TdApi.MessageContent.MessageChatJoinByLink
        or TdApi.MessageContent.MessageChatJoinByRequest
        or TdApi.MessageContent.MessageChatOwnerChanged
        or TdApi.MessageContent.MessageChatOwnerLeft
        or TdApi.MessageContent.MessageChatSetBackground
        or TdApi.MessageContent.MessageChatSetMessageAutoDeleteTime
        or TdApi.MessageContent.MessageChatSetTheme
        or TdApi.MessageContent.MessageChatShared
        or TdApi.MessageContent.MessageChatUpgradeFrom
        or TdApi.MessageContent.MessageChatUpgradeTo
        or TdApi.MessageContent.MessageChecklistTasksAdded
        or TdApi.MessageContent.MessageChecklistTasksDone
        or TdApi.MessageContent.MessageContactRegistered
        or TdApi.MessageContent.MessageCustomServiceAction
        or TdApi.MessageContent.MessageDirectMessagePriceChanged
        or TdApi.MessageContent.MessageForumTopicCreated
        or TdApi.MessageContent.MessageForumTopicEdited
        or TdApi.MessageContent.MessageForumTopicIsClosedToggled
        or TdApi.MessageContent.MessageForumTopicIsHiddenToggled
        or TdApi.MessageContent.MessageGameScore
        or TdApi.MessageContent.MessageGift
        or TdApi.MessageContent.MessageGiftedPremium
        or TdApi.MessageContent.MessageGiftedStars
        or TdApi.MessageContent.MessageGiftedTon
        or TdApi.MessageContent.MessageGiveawayCompleted
        or TdApi.MessageContent.MessageGiveawayCreated
        or TdApi.MessageContent.MessageGiveawayPrizeStars
        or TdApi.MessageContent.MessageGroupCall
        or TdApi.MessageContent.MessageInviteVideoChatParticipants
        or TdApi.MessageContent.MessageManagedBotCreated
        or TdApi.MessageContent.MessagePaidMessagePriceChanged
        or TdApi.MessageContent.MessagePaidMessagesRefunded
        or TdApi.MessageContent.MessagePassportDataReceived
        or TdApi.MessageContent.MessagePassportDataSent
        or TdApi.MessageContent.MessagePaymentRefunded
        or TdApi.MessageContent.MessagePaymentSuccessful
        or TdApi.MessageContent.MessagePaymentSuccessfulBot
        or TdApi.MessageContent.MessagePinMessage
        or TdApi.MessageContent.MessagePollOptionAdded
        or TdApi.MessageContent.MessagePollOptionDeleted
        or TdApi.MessageContent.MessagePremiumGiftCode
        or TdApi.MessageContent.MessageProximityAlertTriggered
        or TdApi.MessageContent.MessageRefundedUpgradedGift
        or TdApi.MessageContent.MessageScreenshotTaken
        or TdApi.MessageContent.MessageSuggestBirthdate
        or TdApi.MessageContent.MessageSuggestedPostApprovalFailed
        or TdApi.MessageContent.MessageSuggestedPostApproved
        or TdApi.MessageContent.MessageSuggestedPostDeclined
        or TdApi.MessageContent.MessageSuggestedPostPaid
        or TdApi.MessageContent.MessageSuggestedPostRefunded
        or TdApi.MessageContent.MessageSuggestProfilePhoto
        or TdApi.MessageContent.MessageSupergroupChatCreate
        or TdApi.MessageContent.MessageUpgradedGift
        or TdApi.MessageContent.MessageUpgradedGiftPurchaseOffer
        or TdApi.MessageContent.MessageUpgradedGiftPurchaseOfferRejected
        or TdApi.MessageContent.MessageUsersShared
        or TdApi.MessageContent.MessageVideoChatEnded
        or TdApi.MessageContent.MessageVideoChatScheduled
        or TdApi.MessageContent.MessageVideoChatStarted
        or TdApi.MessageContent.MessageWebAppDataReceived
        or TdApi.MessageContent.MessageWebAppDataSent;

    internal static string NormalizeKind(string value)
    {
        return value.Trim().Replace("_", "-").ToLowerInvariant() switch
        {
            "chatupgradefrom" => "chat-upgrade-from",
            "chatupgradeto" => "chat-upgrade-to",
            "videonote" => "video-note",
            "voicenote" or "voice-note" => "voice",
            var normalized => normalized
        };
    }

    internal static async Task<JToken> TryGetParticipantCountAsync(TelegramSession tg, long chatId,
        CancellationToken cancellationToken = default, bool retainNativeOperation = true)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var chat = await TelegramOperations.AwaitReadAsync(tg.Client.GetChatAsync(chatId), cancellationToken, retainNativeOperation);
            return chat.Type switch
            {
                TdApi.ChatType.ChatTypeSupergroup group => (await TelegramOperations.AwaitReadAsync(
                    tg.Client.GetSupergroupFullInfoAsync(group.SupergroupId), cancellationToken, retainNativeOperation)).MemberCount,
                TdApi.ChatType.ChatTypeBasicGroup group => (await TelegramOperations.AwaitReadAsync(
                    tg.Client.GetBasicGroupAsync(group.BasicGroupId), cancellationToken, retainNativeOperation)).MemberCount,
                _ => JValue.CreateNull()
            };
        }
        catch (TdException ex) when (ex.Error.Code is 400 or 403 or 404)
        {
            return JValue.CreateNull();
        }
    }
}
