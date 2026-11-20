using System.Globalization;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using TdLib;

namespace TgCli;

internal enum OutputFormat
{
    Tsv,
    Jsonl,
    Json,
    Plain,
    Md
}

internal sealed record ChatRow(long ChatId, string Title, string Type, string? Username, int Unread, string? LastMessage);

internal sealed record MessageRow(long ChatId, long MessageId, string Date, string Sender, string? SenderName,
    string Kind, int? FileId, string Text, long? ReplyToMessageId, string? TgUrl, string? HttpsUrl, string? HttpsFallback,
    string? Query, long OriginalChatId, long OriginalMessageId);

internal static class Output
{
    public static OutputFormat ParseFormat(string format)
    {
        return format.Trim().ToLowerInvariant() switch
        {
            "tsv" => OutputFormat.Tsv,
            "jsonl" => OutputFormat.Jsonl,
            "json" => OutputFormat.Json,
            "plain" or "text" => OutputFormat.Plain,
            "md" or "markdown" => OutputFormat.Md,
            _ => throw new ArgumentException("Format must be one of: json, jsonl, tsv, plain, md.", nameof(format))
        };
    }

    internal static async Task PrintChatsAsync(TelegramSession tg, IReadOnlyCollection<long> chatIds, OutputFormat format,
        JObject coverage, CancellationToken cancellationToken, bool local = false)
    {
        var rows = new List<ChatRow>(chatIds.Count);
        foreach (var chatId in chatIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var chat = await TelegramOperations.AwaitReadAsync(tg.Client.GetChatAsync(chatId), cancellationToken, true);
            rows.Add(await BuildChatRowAsync(tg, chat, local, cancellationToken));
        }

        tg.CheckActive();
        cancellationToken.ThrowIfCancellationRequested();
        PrintChats(rows, format, Console.Out, coverage);
        if (format != OutputFormat.Json) WriteSummary(coverage, cancellationToken);
    }

    internal static async Task PrintChatAsync(TelegramSession tg, TdApi.Chat chat, OutputFormat format,
        CancellationToken cancellationToken)
    {
        var row = await BuildChatRowAsync(tg, chat, cancellationToken: cancellationToken);
        tg.CheckActive();
        cancellationToken.ThrowIfCancellationRequested();
        if (format is OutputFormat.Json or OutputFormat.Jsonl)
            Console.WriteLine(ToChatJson(row).ToString(Formatting.None));
        else
            PrintChats([row], format, Console.Out);
    }

    internal static async Task WriteReadMessagesAsync(TelegramSession tg, IEnumerable<TdApi.Message> messages,
        OutputFormat format, JObject coverage, CancellationToken cancellationToken, bool local = false, bool includeLinks = true)
    {
        await WriteExportedMessagesAsync(Console.Out, tg, messages.Select(ToExportedMessage), format,
            includeLinks: includeLinks, cancellationToken: cancellationToken, local: local, coverage: coverage);
        if (format != OutputFormat.Json) WriteSummary(coverage, cancellationToken);
    }

    internal static ExportedMessage ToExportedMessage(TdApi.Message message) =>
        new(message, message.ChatId, message.ChatId, message.Id);

    internal static Task WriteExportedMessagesAsync(TextWriter writer, TelegramSession tg,
        IEnumerable<ExportedMessage> messages, OutputFormat format, string? title = null, bool includeLinks = false,
        string? fields = null, MediaTranscription? transcription = null, CancellationToken cancellationToken = default,
        bool local = false, JObject? coverage = null) =>
        WriteMessageGroupsAsync(writer, tg, messages.Select(x => (x, (string?)null)), format, title, includeLinks,
            fields, transcription, cancellationToken, local, coverage);

    internal static async Task WriteMessageGroupsAsync(TextWriter writer, TelegramSession tg,
        IEnumerable<(ExportedMessage Item, string? Query)> messages, OutputFormat format, string? title = null,
        bool includeLinks = true, string? fields = null, MediaTranscription? transcription = null,
        CancellationToken cancellationToken = default, bool local = false, JObject? coverage = null)
    {
        tg.CheckActive();
        cancellationToken.ThrowIfCancellationRequested();
        var ordered = messages.GroupBy(x => x.Query, StringComparer.Ordinal).SelectMany(group =>
            group.OrderBy(x => x.Item.Message.Date).ThenBy(x => x.Item.Message.ChatId).ThenBy(x => x.Item.Message.Id));
        if (format is OutputFormat.Jsonl or OutputFormat.Json)
        {
            await WriteJsonMessagesAsync(writer, ordered, format, coverage,
                (item, query, token) => BuildExportedRowAsync(tg, item, includeLinks, fields, transcription, token, local, query),
                cancellationToken);
            return;
        }

        var rows = new List<MessageRow>();
        foreach (var (item, query) in ordered)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var row = await BuildMessageRowAsync(tg, item, query, includeLinks, local, cancellationToken);
            var primary = MessageFiles.GetPrimaryAttachment(item.Message.Content);
            var transcript = primary is null ? null : await MediaTranscription.TranscribeAttachmentAsync(primary, transcription,
                f => MediaTranscription.DownloadAsync(tg, f, cancellationToken, local, item.Message.ChatId, item.Message.Id),
                cancellationToken);
            rows.Add(row with { Text = transcript is null ? row.Text : MediaTranscription.AppendToCaption(row.Text, transcript) });
        }
        tg.CheckActive();
        cancellationToken.ThrowIfCancellationRequested();
        WriteRows(writer, rows, format, title, includeLinks);
    }

    internal static async Task WriteJsonMessagesAsync(TextWriter writer,
        IEnumerable<(ExportedMessage Item, string? Query)> ordered, OutputFormat format, JObject? coverage,
        Func<ExportedMessage, string?, CancellationToken, Task<JObject>> enrich, CancellationToken cancellationToken)
    {
        var rows = format == OutputFormat.Json ? new JArray() : null;
        foreach (var (item, query) in ordered)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var row = await enrich(item, query, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (rows is not null) rows.Add(row);
            else await writer.WriteLineAsync(row.ToString(Formatting.None).AsMemory(), cancellationToken);
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (rows is not null) WriteJsonCollection(writer, rows, coverage);
    }

    internal static void WriteSummary(JObject summary, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Console.Error.WriteLine(summary.ToString(Formatting.None));
    }

    internal static void WriteJsonCollection(TextWriter writer, JArray rows, JObject? coverage)
    {
        if (coverage is null) writer.WriteLine(rows.ToString(Formatting.None));
        else
        {
            coverage["items"] = rows;
            writer.WriteLine(coverage.ToString(Formatting.None));
        }
    }

    internal static async Task<JObject> BuildExportedRowAsync(
        TelegramSession tg, ExportedMessage item, bool includeLinks, string? fields, MediaTranscription? transcription,
        CancellationToken cancellationToken, bool local = false, string? query = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var row = await ExportSchema.BuildMessageAsync(tg, item, includeLinks, transcription, cancellationToken, local, query);
        tg.CheckActive();
        cancellationToken.ThrowIfCancellationRequested();
        return SelectFields(row, fields);
    }

    private static JObject SelectFields(JObject value, string? fields)
    {
        if (string.IsNullOrWhiteSpace(fields))
        {
            return value;
        }

        var names = fields.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .ToHashSet(StringComparer.Ordinal);
        names.UnionWith(["schema", "schema_version"]);
        var unknown = names.Where(name => value.Property(name) is null).ToArray();
        if (unknown.Length > 0)
        {
            throw new ArgumentException($"Unknown export field(s): {string.Join(", ", unknown)}.");
        }
        return new JObject(value.Properties().Where(property => names.Contains(property.Name)));
    }

    internal static void PrintChats(IEnumerable<ChatRow> rows, OutputFormat format, TextWriter writer, JObject? coverage = null)
    {
        switch (format)
        {
            case OutputFormat.Tsv:
                writer.WriteLine("chat_id\ttitle\ttype\tusername\tunread\tlast_message");
                foreach (var row in rows)
                {
                    writer.WriteLine(string.Join('\t',
                        row.ChatId.ToString(CultureInfo.InvariantCulture),
                        Clean(row.Title),
                        row.Type,
                        Clean(row.Username),
                        row.Unread.ToString(CultureInfo.InvariantCulture),
                        Clean(row.LastMessage)));
                }
                break;

            case OutputFormat.Json:
                WriteJsonCollection(writer, new JArray(rows.Select(ToChatJson)), coverage);
                break;

            case OutputFormat.Jsonl:
                foreach (var row in rows)
                    writer.WriteLine(ToChatJson(row).ToString(Formatting.None));
                break;

            case OutputFormat.Plain:
                foreach (var row in rows)
                    writer.WriteLine(row.Title);
                break;

            case OutputFormat.Md:
                foreach (var row in rows)
                {
                    writer.WriteLine($"## {row.Title}");
                    writer.WriteLine();
                    writer.WriteLine($"- Chat ID: `{row.ChatId}`");
                    writer.WriteLine($"- Type: `{row.Type}`");
                    if (!string.IsNullOrWhiteSpace(row.Username))
                    {
                        writer.WriteLine($"- Username: `@{row.Username}`");
                    }

                    writer.WriteLine($"- Unread: `{row.Unread}`");
                    writer.WriteLine($"- Last message: {row.LastMessage}");
                    writer.WriteLine();
                }
                break;
        }
    }

    private static void WriteRows(TextWriter writer, List<MessageRow> rows, OutputFormat format, string? title, bool includeLinks)
    {
        switch (format)
        {
            case OutputFormat.Plain:
                foreach (var row in rows)
                {
                    if (row.Query is not null) writer.Write(Clean(row.Query) + "\t");
                    writer.WriteLine(string.IsNullOrWhiteSpace(row.Text) ? row.Kind : row.Text);
                }
                break;

            case OutputFormat.Tsv:
                var withQueries = rows.Any(row => row.Query is not null);
                var header = includeLinks
                    ? "chat_id\tmessage_id\toriginal_chat_id\toriginal_message_id\tdate\tsender\tsender_name\tkind\tfile_id\treply_to_message_id\ttg_url\thttps_url\thttps_fallback\ttext"
                    : "chat_id\tmessage_id\toriginal_chat_id\toriginal_message_id\tdate\tsender\tsender_name\tkind\tfile_id\treply_to_message_id\ttext";
                writer.WriteLine(withQueries ? "query\t" + header : header);
                foreach (var row in rows)
                {
                    var values = new List<string>
                    {
                        row.ChatId.ToString(CultureInfo.InvariantCulture),
                        row.MessageId.ToString(CultureInfo.InvariantCulture),
                        Wire.Id(row.OriginalChatId),
                        Wire.Id(row.OriginalMessageId),
                        Clean(row.Date),
                        Clean(row.Sender),
                        Clean(row.SenderName),
                        row.Kind,
                        row.FileId?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                        row.ReplyToMessageId?.ToString(CultureInfo.InvariantCulture) ?? string.Empty
                    };

                    if (includeLinks)
                    {
                        values.Add(Clean(row.TgUrl));
                        values.Add(Clean(row.HttpsUrl));
                        values.Add(Clean(row.HttpsFallback));
                    }

                    if (withQueries) values.Insert(0, Clean(row.Query));
                    values.Add(Clean(row.Text));
                    writer.WriteLine(string.Join('\t', values));
                }
                break;

            case OutputFormat.Md:
                if (!string.IsNullOrWhiteSpace(title))
                {
                    writer.WriteLine($"# {title}");
                    writer.WriteLine();
                }

                string? currentDay = null;
                foreach (var row in rows)
                {
                    var day = row.Date.Length >= 10 ? row.Date[..10] : row.Date;
                    if (day != currentDay)
                    {
                        currentDay = day;
                        writer.WriteLine($"## {day}");
                        writer.WriteLine();
                    }

                    var time = row.Date.Length >= 16 ? row.Date[11..16] : row.Date;
                    writer.WriteLine($"**{EscapeMd(FirstNonEmpty(row.SenderName, row.Sender))}, {time}**");
                    if (!string.IsNullOrWhiteSpace(row.Query))
                    {
                        writer.WriteLine();
                        writer.WriteLine($"_Query: `{EscapeMd(row.Query)}`_");
                    }

                    if (includeLinks && !string.IsNullOrWhiteSpace(row.TgUrl))
                    {
                        writer.WriteLine();
                        writer.WriteLine($"_message_id: `{row.MessageId}`; [tg]({row.TgUrl}){FormatOptionalMdLink("https", row.HttpsUrl)}{FormatOptionalMdLink("fallback", row.HttpsFallback)}_");
                    }

                    writer.WriteLine();
                    writer.WriteLine(string.IsNullOrWhiteSpace(row.Text) ? $"_{row.Kind}_" : row.Text);
                    if (row.FileId is not null)
                    {
                        writer.WriteLine();
                        writer.WriteLine(includeLinks && !string.IsNullOrWhiteSpace(row.TgUrl)
                            ? $"_file_id: `{row.FileId}`; kind: `{row.Kind}`; [tg]({row.TgUrl}){FormatOptionalMdLink("https", row.HttpsUrl)}_"
                            : $"_file_id: `{row.FileId}`; kind: `{row.Kind}`_");
                    }

                    writer.WriteLine();
                }
                break;
        }
    }

    internal static JObject ToChatJson(ChatRow row) => new()
    {
        ["chat_id"] = Wire.Id(row.ChatId),
        ["title"] = row.Title,
        ["type"] = row.Type,
        ["username"] = row.Username,
        ["unread"] = row.Unread,
        ["last_message"] = row.LastMessage
    };

    internal static async Task<ChatRow> BuildChatRowAsync(TelegramSession tg, TdApi.Chat chat, bool local = false,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var username = local ? null : await MessageLinks.TryGetUsernameAsync(tg, chat.Id, cancellationToken,
            retainNativeOperation: true);

        return new ChatRow(
            chat.Id,
            chat.Title,
            FormatChatType(chat.Type),
            username,
            chat.UnreadCount,
            SummarizeMessage(chat.LastMessage));
    }

    private static async Task<MessageRow> BuildMessageRowAsync(
        TelegramSession tg,
        ExportedMessage item,
        string? query,
        bool includeLinks,
        bool local,
        CancellationToken cancellationToken)
    {
        var message = item.Message;
        var attachment = MessageFiles.GetPrimaryAttachment(message.Content);
        var links = !includeLinks ? null : local ? Wire.LocalLink(message.ChatId, message.Id)
            : await MessageLinks.BuildAsync(tg, message.ChatId, message.Id, cancellationToken, retainNativeOperation: true);
        if (attachment is not null)
        {
            await AttachmentIndex.RecordAsync(tg.SessionDirectory, message.ChatId, message.Id, [attachment]);
        }
        return new MessageRow(
            message.ChatId,
            message.Id,
            Wire.Timestamp(message.Date),
            FormatSender(message.SenderId),
            local ? null : await FormatSenderNameAsync(tg, message.SenderId, cancellationToken),
            attachment?.Kind ?? MessageFiles.GetKind(message.Content),
            attachment?.FileId,
            MessageFiles.GetMessageText(message.Content),
            GetReplyToMessageId(message.ReplyTo),
            links?.TgUrl,
            links?.HttpsUrl,
            links?.HttpsFallback,
            query,
            item.OriginalChatId,
            item.OriginalMessageId);
    }

    private static string? SummarizeMessage(TdApi.Message? message)
    {
        if (message is null)
        {
            return null;
        }

        var attachment = MessageFiles.GetPrimaryAttachment(message.Content);
        var kind = attachment?.Kind ?? MessageFiles.GetKind(message.Content);
        var text = MessageFiles.GetMessageText(message.Content);
        return string.IsNullOrWhiteSpace(text) ? kind : $"{kind}: {text}";
    }

    private static string FormatChatType(TdApi.ChatType? type)
    {
        return type switch
        {
            TdApi.ChatType.ChatTypePrivate => "private",
            TdApi.ChatType.ChatTypeBasicGroup => "basic-group",
            TdApi.ChatType.ChatTypeSupergroup supergroup => supergroup.IsChannel ? "channel" : "supergroup",
            TdApi.ChatType.ChatTypeSecret => "secret",
            _ => "unknown"
        };
    }

    private static string FormatSender(TdApi.MessageSender? sender)
    {
        return sender switch
        {
            TdApi.MessageSender.MessageSenderUser user => $"user:{user.UserId}",
            TdApi.MessageSender.MessageSenderChat chat => $"chat:{chat.ChatId}",
            _ => string.Empty
        };
    }

    private static async Task<string?> FormatSenderNameAsync(TelegramSession tg, TdApi.MessageSender? sender,
        CancellationToken cancellationToken)
    {
        try
        {
            return sender switch
            {
                TdApi.MessageSender.MessageSenderUser user => FormatUser(await TelegramOperations.AwaitReadAsync(
                    tg.Client.GetUserAsync(user.UserId), cancellationToken, retainNativeOperation: true)),
                TdApi.MessageSender.MessageSenderChat chat => (await TelegramOperations.AwaitReadAsync(
                    tg.Client.GetChatAsync(chat.ChatId), cancellationToken, retainNativeOperation: true)).Title,
                _ => null
            };
        }
        catch (TdException ex) when (ex.Error.Code is 400 or 403 or 404)
        {
            return null;
        }
    }

    private static long? GetReplyToMessageId(TdApi.MessageReplyTo? replyTo)
    {
        return replyTo switch
        {
            TdApi.MessageReplyTo.MessageReplyToMessage messageReply => messageReply.MessageId,
            _ => null
        };
    }

    private static string FormatUser(TdApi.User user)
    {
        var name = FirstNonEmpty($"{user.FirstName} {user.LastName}".Trim(), user.Usernames?.ActiveUsernames?.FirstOrDefault());
        var username = user.Usernames?.ActiveUsernames?.FirstOrDefault();
        return string.IsNullOrWhiteSpace(username) ? name : $"{name} (@{username})";
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        return values.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)) ?? string.Empty;
    }

    internal static string Clean(string? value) => (value ?? string.Empty)
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("\t", "\\t", StringComparison.Ordinal)
        .Replace("\r", "\\r", StringComparison.Ordinal)
        .Replace("\n", "\\n", StringComparison.Ordinal);

    private static string EscapeMd(string value)
    {
        return value.Replace("\\", "\\\\").Replace("`", "\\`");
    }

    private static string FormatOptionalMdLink(string label, string? url)
    {
        return string.IsNullOrWhiteSpace(url) ? string.Empty : $"; [{label}]({url})";
    }
}
