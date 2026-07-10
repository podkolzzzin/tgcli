using TdLib;

namespace TgCli;

internal sealed record MessageLinkInfo(
    long ChatId,
    long MessageId,
    long ShortMessageId,
    string TgUrl,
    string? HttpsUrl,
    string? HttpsFallback);

internal static class MessageLinks
{
    public static long GetShortMessageId(long messageId)
    {
        return ChatHistory.GetShortMessageId(messageId);
    }

    public static string GetTgUrl(long chatId, long messageId)
    {
        return $"tg://openmessage?user_id={chatId}&message_id={GetShortMessageId(messageId)}";
    }

    public static async Task<MessageLinkInfo> BuildAsync(TelegramSession tg, long chatId, long messageId)
    {
        var httpsUrl = await TryGetHttpsUrlAsync(tg, chatId, messageId);
        var username = await TryGetUsernameAsync(tg, chatId);

        return new MessageLinkInfo(
            chatId,
            messageId,
            GetShortMessageId(messageId),
            GetTgUrl(chatId, messageId),
            httpsUrl,
            string.IsNullOrWhiteSpace(username) ? null : $"https://t.me/{username}");
    }

    private static async Task<string?> TryGetHttpsUrlAsync(TelegramSession tg, long chatId, long messageId)
    {
        try
        {
            var link = await tg.Client.GetMessageLinkAsync(
                chatId,
                messageId,
                mediaTimestamp: 0,
                checklistTaskId: 0,
                pollOptionId: string.Empty,
                forAlbum: false,
                inMessageThread: false);

            return string.IsNullOrWhiteSpace(link.Link) ? null : link.Link;
        }
        catch
        {
            return null;
        }
    }

    public static async Task<string?> TryGetUsernameAsync(TelegramSession tg, long chatId)
    {
        try
        {
            var chat = await tg.Client.GetChatAsync(chatId);
            return chat.Type switch
            {
                TdApi.ChatType.ChatTypePrivate privateChat => (await tg.Client.GetUserAsync(privateChat.UserId))
                    .Usernames?.ActiveUsernames?.FirstOrDefault(),
                TdApi.ChatType.ChatTypeSupergroup supergroupChat => (await tg.Client.GetSupergroupAsync(supergroupChat.SupergroupId))
                    .Usernames?.ActiveUsernames?.FirstOrDefault(),
                _ => null
            };
        }
        catch
        {
            return null;
        }
    }
}
