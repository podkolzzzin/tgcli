using System.Globalization;
using TdLib;

namespace TgCli;

internal sealed record MessageLinkInfo(
    long ChatId,
    long MessageId,
    long ShortMessageId,
    string? TgUrl,
    string? HttpsUrl,
    string? HttpsFallback)
{
    public string? TgUrlScope => TgUrl switch
    {
        null => null,
        var url when url.StartsWith("tg://openmessage?", StringComparison.Ordinal) => "client_specific",
        _ => "telegram_message_link"
    };
}

internal static class MessageLinks
{
    private const long SupergroupChatBase = -1_000_000_000_000;
    private const long RegularChannelIdLimit = 1_000_000_000_000 - (1L << 31);

    public static string? GetTgUrl(long chatId, long messageId)
    {
        if (ChatHistory.TryGetPublicPostId(messageId) is not long post) return null;
        if (chatId < SupergroupChatBase && chatId > SupergroupChatBase - RegularChannelIdLimit)
            return FormattableString.Invariant($"tg://privatepost?channel={SupergroupChatBase - chatId}&post={post}");

        // Telegram Android handles user_id for users and chat_id for basic groups.
        if (chatId > 0)
            return FormattableString.Invariant($"tg://openmessage?user_id={chatId}&message_id={post}");
        if (chatId < 0 && chatId > SupergroupChatBase)
            return FormattableString.Invariant($"tg://openmessage?chat_id={-chatId}&message_id={post}");

        return null;
    }

    public static async Task<MessageLinkInfo> BuildAsync(TelegramSession tg, long chatId, long messageId,
        CancellationToken cancellationToken = default, bool retainNativeOperation = false)
    {
        var httpsUrl = await TryGetHttpsUrlAsync(tg, chatId, messageId, cancellationToken, retainNativeOperation);
        var tgUrl = GetTgUrl(chatId, messageId);
        if (httpsUrl is not null)
            tgUrl = ToTgUrl(httpsUrl) ?? tgUrl;
        var username = httpsUrl is null
            ? await TryGetUsernameAsync(tg, chatId, cancellationToken, retainNativeOperation)
            : null;
        return new MessageLinkInfo(
            chatId,
            messageId,
            ChatHistory.GetShortMessageId(messageId),
            tgUrl,
            httpsUrl,
            string.IsNullOrWhiteSpace(username) ? null : $"https://t.me/{username}");
    }

    internal static string? ToTgUrl(string httpsUrl)
    {
        if (!Uri.TryCreate(httpsUrl, UriKind.Absolute, out var uri) || uri.Scheme != "https"
            || !uri.Host.Equals("t.me", StringComparison.OrdinalIgnoreCase))
            return null;

        var parts = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var privateLink = parts.Length > 0 && parts[0] == "c";
        var peerIndex = privateLink ? 1 : 0;
        if (parts.Length != peerIndex + 2 && parts.Length != peerIndex + 3)
            return null;
        if (!long.TryParse(parts[^1], NumberStyles.None, CultureInfo.InvariantCulture, out var post) || post <= 0)
            return null;

        var url = privateLink
            ? $"tg://privatepost?channel={parts[peerIndex]}&post={parts[^1]}"
            : $"tg://resolve?domain={parts[peerIndex]}&post={parts[^1]}";
        if (parts.Length == peerIndex + 3)
            url += $"&thread={parts[peerIndex + 1]}";
        return uri.Query.Length > 1 ? url + "&" + uri.Query[1..] : url;
    }

    private static async Task<string?> TryGetHttpsUrlAsync(TelegramSession tg, long chatId, long messageId,
        CancellationToken cancellationToken, bool retainNativeOperation)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var link = await TelegramOperations.AwaitReadAsync(tg.Client.GetMessageLinkAsync(
                chatId, messageId, mediaTimestamp: 0, checklistTaskId: 0, pollOptionId: string.Empty,
                forAlbum: false, inMessageThread: false), cancellationToken, retainNativeOperation);
            return string.IsNullOrWhiteSpace(link.Link) ? null : link.Link;
        }
        catch (TdException ex) when (ex.Error.Code is 400 or 403 or 404)
        {
            return null;
        }
    }

    public static async Task<string?> TryGetUsernameAsync(TelegramSession tg, long chatId,
        CancellationToken cancellationToken = default, bool retainNativeOperation = false)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var chat = await TelegramOperations.AwaitReadAsync(tg.Client.GetChatAsync(chatId), cancellationToken, retainNativeOperation);
            return chat.Type switch
            {
                TdApi.ChatType.ChatTypePrivate privateChat => (await TelegramOperations.AwaitReadAsync(
                    tg.Client.GetUserAsync(privateChat.UserId), cancellationToken, retainNativeOperation))
                    .Usernames?.ActiveUsernames?.FirstOrDefault(),
                TdApi.ChatType.ChatTypeSupergroup supergroupChat => (await TelegramOperations.AwaitReadAsync(
                    tg.Client.GetSupergroupAsync(supergroupChat.SupergroupId), cancellationToken, retainNativeOperation))
                    .Usernames?.ActiveUsernames?.FirstOrDefault(),
                _ => null
            };
        }
        catch (TdException ex) when (ex.Error.Code is 400 or 403 or 404)
        {
            return null;
        }
    }
}
