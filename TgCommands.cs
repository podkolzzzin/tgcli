using ConsoleAppFramework;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using TdLib;

namespace TgCli;

public sealed class TgCommands
{
    /// <summary>
    /// Sign in to Telegram and persist the local TDLib session.
    /// </summary>
    /// <param name="apiId">Telegram API id. Can also be set with TGCLI_API_ID.</param>
    /// <param name="apiHash">Telegram API hash. Can also be set with TGCLI_API_HASH.</param>
    /// <param name="phone">Phone number in international format. If omitted, tgcli prompts.</param>
    /// <param name="session">Session directory. Defaults to ~/.local/share/tgcli.</param>
    public async Task Login(int apiId = 0, string? apiHash = null, string? phone = null, string? session = null)
    {
        await using var tg = await TelegramSession.CreateAsync(session, apiId, apiHash, saveConfig: true);
        await tg.LoginAsync(phone);
    }

    /// <summary>
    /// Search chats by title or username.
    /// </summary>
    /// <param name="query">Search text.</param>
    /// <param name="limit">Maximum number of chats to print.</param>
    /// <param name="server">Also ask Telegram servers for public chats.</param>
    /// <param name="format">Output format: tsv, jsonl, md.</param>
    /// <param name="session">Session directory. Defaults to ~/.local/share/tgcli.</param>
    public async Task Search(string query, int limit = 20, bool server = false, string format = "tsv", string? session = null)
    {
        await using var tg = await TelegramSession.CreateReadyAsync(session);
        var chats = server
            ? await tg.Client.SearchChatsOnServerAsync(query, ClampLimit(limit, 100))
            : await tg.Client.SearchChatsAsync(query, ClampLimit(limit, 100));

        await Output.PrintChatsAsync(tg, chats.ChatIds, format);
    }

    /// <summary>
    /// Download a Telegram file by message, file id, or remote file id.
    /// </summary>
    /// <param name="type">File type: voice, attach/document, audio, video, photo, animation, video-note, file. Optional for --chat-id/--message-id.</param>
    /// <param name="attachmentId">TDLib integer file id printed by search/messages, or a remote file id string.</param>
    /// <param name="chatId">Chat id that contains the attachment.</param>
    /// <param name="messageId">Message id that contains the attachment.</param>
    /// <param name="output">Destination file or directory. Defaults to the current directory.</param>
    /// <param name="session">Session directory. Defaults to ~/.local/share/tgcli.</param>
    public async Task Download(
        string? type = null,
        string? attachmentId = null,
        long chatId = 0,
        long messageId = 0,
        string output = ".",
        string? session = null)
    {
        await using var tg = await TelegramSession.CreateReadyAsync(session);
        var file = await ResolveDownloadFileAsync(tg, type, attachmentId, chatId, messageId);

        file = await DownloadFileWithRefreshAsync(tg, file, type, chatId, messageId);

        if (string.IsNullOrWhiteSpace(file.Local?.Path) || !File.Exists(file.Local.Path))
        {
            throw new InvalidOperationException($"TDLib did not provide a downloaded local path for file_id={file.Id}.");
        }

        var destination = ResolveDownloadDestination(output, file);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Copy(file.Local.Path, destination, overwrite: true);

        Console.WriteLine(destination);
    }

    private static async Task<TdApi.File> ResolveDownloadFileAsync(
        TelegramSession tg,
        string? type,
        string? attachmentId,
        long chatId,
        long messageId)
    {
        if (chatId != 0 && messageId != 0)
        {
            var message = await tg.Client.GetMessageAsync(chatId, messageId);
            var attachment = MessageFiles.GetPrimaryAttachment(message.Content, type)
                ?? throw new InvalidOperationException($"Message {chatId}/{messageId} does not contain a downloadable attachment matching type '{type ?? "file"}'.");

            return attachment.File;
        }

        if (string.IsNullOrWhiteSpace(attachmentId))
        {
            throw new ArgumentException("Provide either --chat-id and --message-id, or --attachment-id with --type.");
        }

        var kind = AttachmentKinds.Parse(type ?? "file");
        return int.TryParse(attachmentId, out var fileId)
            ? await tg.Client.GetFileAsync(fileId)
            : await tg.Client.GetRemoteFileAsync(attachmentId, AttachmentKinds.ToFileType(kind));
    }

    private static async Task<TdApi.File> DownloadFileWithRefreshAsync(
        TelegramSession tg,
        TdApi.File file,
        string? type,
        long chatId,
        long messageId)
    {
        try
        {
            return await DownloadFileAsync(tg, file);
        }
        catch (TdException) when (chatId != 0 && messageId != 0)
        {
            var refreshedMessage = await tg.Client.GetMessageAsync(chatId, messageId);
            var refreshedAttachment = MessageFiles.GetPrimaryAttachment(refreshedMessage.Content, type)
                ?? throw new InvalidOperationException($"Message {chatId}/{messageId} no longer contains a downloadable attachment matching type '{type ?? "file"}'.");

            return await DownloadFileAsync(tg, refreshedAttachment.File);
        }
    }

    private static async Task<TdApi.File> DownloadFileAsync(TelegramSession tg, TdApi.File file)
    {
        if (file.Local is { IsDownloadingCompleted: true } &&
            !string.IsNullOrWhiteSpace(file.Local.Path) &&
            File.Exists(file.Local.Path))
        {
            return file;
        }

        return await tg.Client.DownloadFileAsync(file.Id, priority: 32, offset: 0, limit: 0, synchronous: true);
    }

    private static int ClampLimit(int value, int max)
    {
        if (value <= 0)
        {
            return 1;
        }

        return Math.Min(value, max);
    }

    private static string ResolveDownloadDestination(string output, TdLib.TdApi.File file)
    {
        var fullOutput = Path.GetFullPath(output);
        var looksLikeDirectory = output.EndsWith(Path.DirectorySeparatorChar)
            || output.EndsWith(Path.AltDirectorySeparatorChar)
            || Directory.Exists(fullOutput);

        if (!looksLikeDirectory)
        {
            return fullOutput;
        }

        var fileName = Path.GetFileName(file.Local.Path);
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
    public async Task Message(long chatId, long messageId, string format = "json", string? session = null)
    {
        await using var tg = await TelegramSession.CreateReadyAsync(session);
        var links = await MessageLinks.BuildAsync(tg, chatId, messageId);

        switch (format.Trim().ToLowerInvariant())
        {
            case "json" or "jsonl":
                Console.WriteLine(JsonConvert.SerializeObject(ToLinkJson(links), Formatting.None));
                break;
            case "tsv":
                Console.WriteLine("chat_id\tmessage_id\tshort_message_id\ttg_url\thttps_url\thttps_fallback");
                Console.WriteLine(string.Join('\t',
                    links.ChatId,
                    links.MessageId,
                    links.ShortMessageId,
                    links.TgUrl,
                    links.HttpsUrl ?? string.Empty,
                    links.HttpsFallback ?? string.Empty));
                break;
            case "plain":
                Console.WriteLine(links.TgUrl);
                break;
            default:
                throw new ArgumentException("Format must be one of: json, jsonl, tsv, plain.", nameof(format));
        }
    }

    private static JObject ToLinkJson(MessageLinkInfo links)
    {
        return new JObject
        {
            ["chat_id"] = links.ChatId,
            ["message_id"] = links.MessageId,
            ["short_message_id"] = links.ShortMessageId,
            ["tg_url"] = links.TgUrl,
            ["https_url"] = links.HttpsUrl is null ? JValue.CreateNull() : links.HttpsUrl,
            ["https_fallback"] = links.HttpsFallback is null ? JValue.CreateNull() : links.HttpsFallback
        };
    }
}

public sealed class MessageCommands
{
    /// <summary>
    /// Get one full message as JSON, including extracted files and links.
    /// </summary>
    /// <param name="chatId">Chat id that contains the message.</param>
    /// <param name="messageId">Message id.</param>
    /// <param name="session">Session directory. Defaults to ~/.local/share/tgcli.</param>
    public async Task Get(long chatId, long messageId, string? session = null)
    {
        await using var tg = await TelegramSession.CreateReadyAsync(session);
        var message = await tg.Client.GetMessageAsync(chatId, messageId);
        var links = await MessageLinks.BuildAsync(tg, chatId, messageId);
        var senderName = await FormatSenderNameAsync(tg, message.SenderId);

        var payload = new JObject
        {
            ["chat_id"] = message.ChatId,
            ["message_id"] = message.Id,
            ["kind"] = MessageFiles.GetKind(message.Content),
            ["text"] = MessageFiles.GetMessageText(message.Content),
            ["sender"] = FormatSender(message.SenderId),
            ["sender_name"] = senderName,
            ["links"] = LinkCommandsJson(links),
            ["files"] = new JArray(MessageFiles.GetAllFiles(message).Select(ToFileJson)),
            ["message"] = JObject.FromObject(message, JsonSerializer.CreateDefault())
        };

        Console.WriteLine(payload.ToString(Formatting.None));
    }

    private static JObject LinkCommandsJson(MessageLinkInfo links)
    {
        return new JObject
        {
            ["short_message_id"] = links.ShortMessageId,
            ["tg_url"] = links.TgUrl,
            ["https_url"] = links.HttpsUrl is null ? JValue.CreateNull() : links.HttpsUrl,
            ["https_fallback"] = links.HttpsFallback is null ? JValue.CreateNull() : links.HttpsFallback
        };
    }

    private static JObject ToFileJson(MessageFile file)
    {
        return new JObject
        {
            ["kind"] = file.Kind,
            ["file_id"] = file.FileId,
            ["remote_id"] = file.RemoteId is null ? JValue.CreateNull() : file.RemoteId,
            ["unique_id"] = file.UniqueId is null ? JValue.CreateNull() : file.UniqueId,
            ["file_name"] = file.FileName is null ? JValue.CreateNull() : file.FileName,
            ["mime_type"] = file.MimeType is null ? JValue.CreateNull() : file.MimeType,
            ["width"] = file.Width is null ? JValue.CreateNull() : file.Width,
            ["height"] = file.Height is null ? JValue.CreateNull() : file.Height,
            ["size"] = file.Size is null ? JValue.CreateNull() : file.Size,
            ["property_path"] = file.PropertyPath
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
}

public sealed class ChatCommands
{
    /// <summary>
    /// List chats from the main chat list.
    /// </summary>
    /// <param name="limit">Maximum number of chats to print.</param>
    /// <param name="format">Output format: tsv, jsonl, md.</param>
    /// <param name="session">Session directory. Defaults to ~/.local/share/tgcli.</param>
    public async Task List(int limit = 50, string format = "tsv", string? session = null)
    {
        await using var tg = await TelegramSession.CreateReadyAsync(session);
        var chats = await tg.Client.GetChatsAsync(new TdLib.TdApi.ChatList.ChatListMain(), ClampLimit(limit, 1000));
        await Output.PrintChatsAsync(tg, chats.ChatIds, format);
    }

    /// <summary>
    /// Resolve a public username to a chat.
    /// </summary>
    /// <param name="username">Telegram username, with or without @.</param>
    /// <param name="format">Output format: tsv, jsonl, md.</param>
    /// <param name="session">Session directory. Defaults to ~/.local/share/tgcli.</param>
    public async Task Resolve(string username, string format = "tsv", string? session = null)
    {
        await using var tg = await TelegramSession.CreateReadyAsync(session);
        var normalized = username.Trim().TrimStart('@');
        var chat = await tg.Client.SearchPublicChatAsync(normalized);
        await Output.PrintChatAsync(tg, chat, format);
    }

    /// <summary>
    /// Print messages from a chat.
    /// </summary>
    /// <param name="chatId">Chat id from tgcli chat list/search/resolve.</param>
    /// <param name="limit">Maximum messages for a single page.</param>
    /// <param name="fromMessageId">Start before this message id. Use the oldest printed id as the next page cursor.</param>
    /// <param name="offset">TDLib history offset, usually 0.</param>
    /// <param name="local">Read local database only.</param>
    /// <param name="all">Fetch all pages until history ends or max-pages is reached.</param>
    /// <param name="maxPages">Maximum pages to fetch with --all.</param>
    /// <param name="format">Output format: tsv, jsonl, md.</param>
    /// <param name="session">Session directory. Defaults to ~/.local/share/tgcli.</param>
    public async Task Messages(
        long chatId,
        int limit = 30,
        long fromMessageId = 0,
        int offset = 0,
        bool local = false,
        bool all = false,
        int maxPages = 1000,
        string format = "tsv",
        string? session = null)
    {
        await using var tg = await TelegramSession.CreateReadyAsync(session);
        IEnumerable<TdApi.Message> messages = all
            ? await FetchHistoryAsync(tg, chatId, fromMessageId, local, maxPages)
            : (await tg.Client.GetChatHistoryAsync(chatId, fromMessageId, offset, ClampLimit(limit, 100), local)).Messages_;

        await Output.PrintMessagesAsync(tg, messages, format);
    }

    /// <summary>
    /// Search messages inside a chat.
    /// </summary>
    /// <param name="chatId">Chat id from tgcli chat list/search/resolve.</param>
    /// <param name="query">Search text. Use an empty string with --type to search attachments.</param>
    /// <param name="queries">File with one search query per line.</param>
    /// <param name="type">Filter: all, voice, attach/document, audio, video, photo, animation, video-note.</param>
    /// <param name="limit">Maximum messages for a single page.</param>
    /// <param name="fromMessageId">Start before this message id. Use the oldest printed id as the next page cursor.</param>
    /// <param name="offset">TDLib search offset, usually 0.</param>
    /// <param name="all">Fetch all pages until search results end or max-pages is reached.</param>
    /// <param name="maxPages">Maximum pages to fetch with --all.</param>
    /// <param name="format">Output format: tsv, jsonl, md.</param>
    /// <param name="session">Session directory. Defaults to ~/.local/share/tgcli.</param>
    public async Task Search(
        long chatId,
        string query = "",
        string? queries = null,
        string type = "all",
        int limit = 30,
        long fromMessageId = 0,
        int offset = 0,
        bool all = false,
        int maxPages = 1000,
        string format = "tsv",
        string? session = null)
    {
        await using var tg = await TelegramSession.CreateReadyAsync(session);
        var searchQueries = LoadQueries(query, queries);

        foreach (var searchQuery in searchQueries)
        {
            IEnumerable<TdApi.Message> messages = all
                ? await FetchSearchAsync(tg, chatId, searchQuery, type, fromMessageId, maxPages)
                : (await tg.Client.SearchChatMessagesAsync(
                    chatId,
                    topicId: null!,
                    searchQuery,
                    senderId: null!,
                    fromMessageId,
                    offset,
                    ClampLimit(limit, 100),
                    AttachmentKinds.ToSearchFilter(type))).Messages;

            await Output.PrintMessagesAsync(tg, messages, format, searchQuery);
        }
    }

    /// <summary>
    /// Export chat history to Markdown, JSONL, or TSV.
    /// </summary>
    /// <param name="chatId">Chat id from tgcli chat list/search/resolve.</param>
    /// <param name="output">Destination file, or - for stdout.</param>
    /// <param name="format">Output format: md, jsonl, tsv.</param>
    /// <param name="all">Fetch all pages until history ends or max-pages is reached.</param>
    /// <param name="maxPages">Maximum pages to fetch with --all.</param>
    /// <param name="local">Read local database only.</param>
    /// <param name="includeLinks">Include tg:// and https links for each exported message.</param>
    /// <param name="session">Session directory. Defaults to ~/.local/share/tgcli.</param>
    public async Task Export(
        long chatId,
        string output = "-",
        string format = "md",
        bool all = true,
        int maxPages = 1000,
        bool local = false,
        bool includeLinks = false,
        string? session = null)
    {
        await using var tg = await TelegramSession.CreateReadyAsync(session);
        var chat = await tg.Client.GetChatAsync(chatId);
        IEnumerable<TdApi.Message> messages = all
            ? await FetchSearchAsync(tg, chatId, query: "", type: "all", fromMessageId: 0, maxPages)
            : (await tg.Client.GetChatHistoryAsync(chatId, fromMessageId: 0, offset: 0, limit: 100, onlyLocal: local)).Messages_;

        var parsedFormat = Output.ParseFormat(format);
        if (output == "-")
        {
            await Output.WriteMessagesAsync(Console.Out, tg, messages, parsedFormat, chat.Title, includeLinks: includeLinks);
            return;
        }

        var fullPath = Path.GetFullPath(output);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        await using var stream = File.CreateText(fullPath);
        await Output.WriteMessagesAsync(stream, tg, messages, parsedFormat, chat.Title, includeLinks: includeLinks);
    }

    private static async Task<List<TdApi.Message>> FetchHistoryAsync(
        TelegramSession tg,
        long chatId,
        long fromMessageId,
        bool local,
        int maxPages)
    {
        var messages = new List<TdApi.Message>();
        var seen = new HashSet<long>();
        var cursor = fromMessageId;

        for (var page = 0; page < Math.Max(1, maxPages); page++)
        {
            var result = await tg.Client.GetChatHistoryAsync(chatId, cursor, offset: 0, limit: 100, onlyLocal: local);
            var pageMessages = result.Messages_.Where(x => seen.Add(x.Id)).ToArray();
            if (pageMessages.Length == 0)
            {
                break;
            }

            messages.AddRange(pageMessages);
            cursor = pageMessages.Min(x => x.Id);

            if (result.Messages_.Length < 100)
            {
                break;
            }
        }

        return messages;
    }

    private static async Task<List<TdApi.Message>> FetchSearchAsync(
        TelegramSession tg,
        long chatId,
        string query,
        string type,
        long fromMessageId,
        int maxPages)
    {
        var messages = new List<TdApi.Message>();
        var seen = new HashSet<long>();
        var cursor = fromMessageId;

        for (var page = 0; page < Math.Max(1, maxPages); page++)
        {
            var result = await tg.Client.SearchChatMessagesAsync(
                chatId,
                topicId: null!,
                query,
                senderId: null!,
                cursor,
                offset: 0,
                limit: 100,
                AttachmentKinds.ToSearchFilter(type));

            var pageMessages = result.Messages.Where(x => seen.Add(x.Id)).ToArray();
            if (pageMessages.Length == 0)
            {
                break;
            }

            messages.AddRange(pageMessages);
            cursor = pageMessages.Min(x => x.Id);

            if (result.Messages.Length < 100)
            {
                break;
            }
        }

        return messages;
    }

    private static string[] LoadQueries(string query, string? queries)
    {
        if (string.IsNullOrWhiteSpace(queries))
        {
            return [query];
        }

        return File.ReadLines(queries)
            .Select(x => x.Trim())
            .Where(x => x.Length > 0 && !x.StartsWith('#'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static int ClampLimit(int value, int max)
    {
        if (value <= 0)
        {
            return 1;
        }

        return Math.Min(value, max);
    }
}
