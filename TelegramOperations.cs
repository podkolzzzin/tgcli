using Newtonsoft.Json.Linq;
using TdLib;

namespace TgCli;

// Data operations shared by the console commands and MCP; no console writes.
internal static class TelegramOperations
{
    internal static Task<TdApi.Chats> ListChatsAsync(TelegramSession tg, int limit) =>
        tg.Client.GetChatsAsync(new TdApi.ChatList.ChatListMain(), limit);

    internal static Task<TdApi.Chats> SearchChatsAsync(TelegramSession tg, string query, int limit, bool server) =>
        server ? tg.Client.SearchChatsOnServerAsync(query, limit) : tg.Client.SearchChatsAsync(query, limit);

    internal static Task<TdApi.Chat> ResolveChatAsync(TelegramSession tg, string username)
    {
        username = username.Trim().TrimStart('@');
        if (username.Length == 0) throw new ArgumentException("username must not be empty.");
        return tg.Client.SearchPublicChatAsync(username);
    }

    internal static async Task<JObject> StatsAsync(TelegramSession tg, long chatId, int topicId,
        string type, int maxPages, bool local, int requestTimeout, CancellationToken cancellationToken, bool retainNativeOperation = false)
    {
        if (topicId < 0)
        {
            throw new ArgumentException("--topic-id must be zero or greater.", nameof(topicId));
        }
        if (topicId > 0 && local)
        {
            throw new ArgumentException("--local is not supported with --topic-id.", nameof(local));
        }

        _ = AttachmentKinds.Parse(type);
        var topicHistory = topicId > 0
            ? await ForumTopicHistory.FetchAsync(
                tg,
                chatId,
                topicId,
                fromMessageId: 0,
                offset: 0,
                limit: 100,
                all: true,
                maxPages,
                requestTimeout,
                cancellationToken, retainNativeOperation)
            : null;
        var history = topicId == 0
            ? await ChatHistory.FetchAsync(tg, chatId, all: true, local, maxPages, followMigrations: true, cancellationToken)
            : null;
        cancellationToken.ThrowIfCancellationRequested();
        var messages = topicHistory is not null
            ? topicHistory.Messages.ToArray()
            : history!.Messages.Select(x => x.Message).ToArray();
        var complete = topicHistory?.Complete ?? history!.Complete;
        var terminationReason = topicHistory?.TerminationReason ?? history!.TerminationReason;
        var pagesFetched = topicHistory?.PagesFetched ?? history!.PagesFetched;
        var attachments = AttachmentStatistics.Build(messages, type);
        var payload = new JObject
        {
            ["chat_id"] = chatId,
            ["topic_id"] = topicId == 0 ? JValue.CreateNull() : topicId,
            ["count"] = messages.Length,
            ["count_kind"] = complete ? "exact" : "estimated",
            ["complete"] = complete,
            ["termination_reason"] = terminationReason,
            ["pages_fetched"] = pagesFetched,
            ["first_timestamp"] = messages.Length == 0 ? JValue.CreateNull() : DateTimeOffset.FromUnixTimeSeconds(messages.Min(x => x.Date)).ToString("O"),
            ["last_timestamp"] = messages.Length == 0 ? JValue.CreateNull() : DateTimeOffset.FromUnixTimeSeconds(messages.Max(x => x.Date)).ToString("O"),
            ["participant_count"] = await ChatCommands.TryGetParticipantCountAsync(tg, chatId),
            ["attachments"] = attachments,
            ["migrations"] = new JArray(history?.SourceChats ?? [chatId])
        };

        return payload;
    }

    internal static async Task<List<TdApi.Message>> ContextAsync(TelegramSession tg, long chatId,
        long messageId, int before, int after, bool followReplyChain, CancellationToken cancellationToken)
    {
        var window = await tg.Client.GetChatHistoryAsync(
            chatId,
            messageId,
            offset: -Math.Max(0, after),
            limit: Math.Clamp(before + after + 1, 1, 100),
            onlyLocal: false);
        var messages = window.Messages_.ToList();
        var seen = messages.Select(x => (x.ChatId, x.Id)).ToHashSet();
        if (followReplyChain)
        {
            var current = await tg.Client.GetMessageAsync(chatId, messageId);
            for (var depth = 0; depth < 100 && current.ReplyTo is TdApi.MessageReplyTo.MessageReplyToMessage reply; depth++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var replyChatId = ChatHistory.GetLongProperty(reply, "ChatId");
                if (replyChatId is null or 0) replyChatId = current.ChatId;
                try
                {
                    current = await tg.Client.GetMessageAsync(replyChatId.Value, reply.MessageId);
                    if (seen.Add((current.ChatId, current.Id))) messages.Add(current);
                }
                catch
                {
                    break;
                }
            }
        }

        return messages;
    }
}
