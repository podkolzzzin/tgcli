using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
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

internal sealed record ChatRow(
    [property: JsonPropertyName("chat_id")]
    long ChatId,
    [property: JsonPropertyName("title")]
    string Title,
    [property: JsonPropertyName("type")]
    string Type,
    [property: JsonPropertyName("username")]
    string Username,
    [property: JsonPropertyName("unread")]
    int Unread,
    [property: JsonPropertyName("last_message")]
    string LastMessage);

internal sealed record MessageRow(
    [property: JsonPropertyName("chat_id")]
    long ChatId,
    [property: JsonPropertyName("message_id")]
    long MessageId,
    [property: JsonPropertyName("date")]
    string Date,
    [property: JsonPropertyName("sender")]
    string Sender,
    [property: JsonPropertyName("sender_name")]
    string SenderName,
    [property: JsonPropertyName("kind")]
    string Kind,
    [property: JsonPropertyName("file_id")]
    int? FileId,
    [property: JsonPropertyName("text")]
    string Text,
    [property: JsonPropertyName("reply_to_message_id")]
    long? ReplyToMessageId,
    [property: JsonPropertyName("tg_url")] string? TgUrl = null,
    [property: JsonPropertyName("https_url")] string? HttpsUrl = null,
    [property: JsonPropertyName("https_fallback")] string? HttpsFallback = null,
    [property: JsonPropertyName("query")]
    string? Query = null,
    [property: JsonPropertyName("original_chat_id")]
    long? OriginalChatId = null,
    [property: JsonPropertyName("original_message_id")]
    long? OriginalMessageId = null);

internal static class Output
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

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

    public static async Task PrintChatsAsync(TelegramSession tg, IReadOnlyCollection<long> chatIds, string format = "tsv")
    {
        var rows = new List<ChatRow>();
        foreach (var chatId in chatIds)
        {
            rows.Add(await BuildChatRowAsync(tg, await tg.Client.GetChatAsync(chatId)));
        }

        PrintChats(rows, ParseFormat(format), Console.Out);
    }

    public static async Task PrintChatAsync(TelegramSession tg, TdApi.Chat chat, string format = "tsv")
    {
        PrintChats([await BuildChatRowAsync(tg, chat)], ParseFormat(format), Console.Out);
    }

    public static async Task PrintMessagesAsync(
        TelegramSession tg,
        IEnumerable<TdApi.Message> messages,
        string format = "tsv",
        string? query = null,
        bool includeLinks = false)
    {
        await WriteMessagesAsync(Console.Out, tg, messages, ParseFormat(format), query: query, includeLinks: includeLinks);
    }

    public static async Task WriteMessagesAsync(
        TextWriter writer,
        TelegramSession tg,
        IEnumerable<TdApi.Message> messages,
        OutputFormat format,
        string? title = null,
        string? query = null,
        bool includeLinks = false)
    {
        var rows = new List<MessageRow>();
        foreach (var message in messages.OrderBy(x => x.Id))
        {
            rows.Add(await BuildMessageRowAsync(tg, message, query, includeLinks));
        }

        WriteRows(writer, rows, format, title, includeLinks);
    }

    public static async Task WriteExportedMessagesAsync(
        TextWriter writer,
        TelegramSession tg,
        IEnumerable<ExportedMessage> messages,
        OutputFormat format,
        string? title = null,
        bool includeLinks = false,
        string? fields = null,
        string? transcribeCommand = null)
    {
        if (format is OutputFormat.Jsonl or OutputFormat.Json)
        {
            var objects = new List<JObject>();
            foreach (var item in messages.OrderBy(x => x.Message.Date).ThenBy(x => x.Message.Id))
            {
                objects.Add(SelectFields(
                    await ExportSchema.BuildMessageAsync(tg, item, includeLinks, transcribeCommand),
                    fields));
            }

            if (format is OutputFormat.Jsonl)
            {
                foreach (var value in objects) writer.WriteLine(value.ToString(Newtonsoft.Json.Formatting.None));
            }
            else
            {
                writer.WriteLine(new JArray(objects).ToString(Newtonsoft.Json.Formatting.None));
            }
            return;
        }

        var rows = new List<MessageRow>();
        foreach (var item in messages.OrderBy(x => x.Message.Id))
        {
            rows.Add((await BuildMessageRowAsync(tg, item.Message, null, includeLinks)) with
            {
                OriginalChatId = item.OriginalChatId,
                OriginalMessageId = item.OriginalMessageId
            });
        }

        WriteRows(writer, rows, format, title, includeLinks);
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

    private static void PrintChats(IEnumerable<ChatRow> rows, OutputFormat format, TextWriter writer)
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

            case OutputFormat.Jsonl:
                foreach (var row in rows)
                {
                    writer.WriteLine(JsonSerializer.Serialize(row, JsonOptions));
                }
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
                foreach (var row in rows.OrderByDescending(x => x.MessageId))
                {
                    writer.WriteLine(string.IsNullOrWhiteSpace(row.Text) ? row.Kind : row.Text);
                }
                break;

            case OutputFormat.Tsv:
                writer.WriteLine(includeLinks
                    ? "chat_id\tmessage_id\toriginal_chat_id\toriginal_message_id\tdate\tsender\tsender_name\tkind\tfile_id\treply_to_message_id\ttg_url\thttps_url\thttps_fallback\ttext"
                    : "chat_id\tmessage_id\toriginal_chat_id\toriginal_message_id\tdate\tsender\tsender_name\tkind\tfile_id\treply_to_message_id\ttext");
                foreach (var row in rows.OrderByDescending(x => x.MessageId))
                {
                    var values = new List<string>
                    {
                        row.ChatId.ToString(CultureInfo.InvariantCulture),
                        row.MessageId.ToString(CultureInfo.InvariantCulture),
                        row.OriginalChatId?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                        row.OriginalMessageId?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
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

                    values.Add(Clean(row.Text));
                    writer.WriteLine(string.Join('\t', values));
                }
                break;

            case OutputFormat.Jsonl:
                foreach (var row in rows)
                {
                    writer.WriteLine(JsonSerializer.Serialize(ToJsonRow(row, includeLinks), JsonOptions));
                }
                break;

            case OutputFormat.Json:
                writer.WriteLine(JsonSerializer.Serialize(rows.Select(x => ToJsonRow(x, includeLinks)), JsonOptions));
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

    private static async Task<ChatRow> BuildChatRowAsync(TelegramSession tg, TdApi.Chat chat)
    {
        var username = await MessageLinks.TryGetUsernameAsync(tg, chat.Id) ?? string.Empty;

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
        TdApi.Message message,
        string? query,
        bool includeLinks)
    {
        var attachment = MessageFiles.GetPrimaryAttachment(message.Content);
        var links = includeLinks ? await MessageLinks.BuildAsync(tg, message.ChatId, message.Id) : null;
        return new MessageRow(
            message.ChatId,
            message.Id,
            DateTimeOffset.FromUnixTimeSeconds(message.Date).LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            FormatSender(message.SenderId),
            await FormatSenderNameAsync(tg, message.SenderId),
            attachment?.Kind ?? MessageFiles.GetKind(message.Content),
            attachment?.FileId,
            MessageFiles.GetMessageText(message.Content),
            GetReplyToMessageId(message.ReplyTo),
            links?.TgUrl,
            links?.HttpsUrl,
            links?.HttpsFallback,
            query);
    }

    private static string SummarizeMessage(TdApi.Message? message)
    {
        if (message is null)
        {
            return string.Empty;
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

    private static async Task<string> FormatSenderNameAsync(TelegramSession tg, TdApi.MessageSender? sender)
    {
        try
        {
            return sender switch
            {
                TdApi.MessageSender.MessageSenderUser user => FormatUser(await tg.Client.GetUserAsync(user.UserId)),
                TdApi.MessageSender.MessageSenderChat chat => (await tg.Client.GetChatAsync(chat.ChatId)).Title,
                _ => string.Empty
            };
        }
        catch
        {
            return string.Empty;
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

    private static string Clean(string? value)
    {
        return (value ?? string.Empty)
            .Replace('\t', ' ')
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Trim();
    }

    private static string EscapeMd(string value)
    {
        return value.Replace("\\", "\\\\").Replace("`", "\\`");
    }

    private static object ToJsonRow(MessageRow row, bool includeLinks)
    {
        if (includeLinks)
        {
            return row;
        }

        return new
        {
            row.ChatId,
            row.MessageId,
            row.Date,
            row.Sender,
            row.SenderName,
            row.Kind,
            row.FileId,
            row.Text,
            row.ReplyToMessageId,
            row.Query,
            row.OriginalChatId,
            row.OriginalMessageId
        };
    }

    private static string FormatOptionalMdLink(string label, string? url)
    {
        return string.IsNullOrWhiteSpace(url) ? string.Empty : $"; [{label}]({url})";
    }
}
