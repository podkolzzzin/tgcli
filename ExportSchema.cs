using Newtonsoft.Json.Linq;
using TdLib;

namespace TgCli;

internal static class ExportSchema
{
    public const string Name = "tgcli.message";
    public const string Version = "8.0";

    public static async Task<JObject> BuildMessageAsync(
        TelegramSession tg,
        ExportedMessage item,
        bool includeLinks,
        MediaTranscription? transcription = null,
        CancellationToken cancellationToken = default,
        bool local = false,
        string? query = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var message = item.Message;
        var sender = local ? GetSenderIdentity(message.SenderId) : await ResolveSenderAsync(tg, message.SenderId, cancellationToken);
        var (replyChatId, replyMessageId) = GetReplyTarget(message);
        var replyPreview = await ResolveReplyPreviewAsync(tg, replyChatId, replyMessageId, local, cancellationToken);
        var links = !includeLinks ? null : local
            ? Wire.LocalLink(message.ChatId, message.Id)
            : await MessageLinks.BuildAsync(tg, message.ChatId, message.Id, cancellationToken, retainNativeOperation: true);
        cancellationToken.ThrowIfCancellationRequested();
        var attachments = MessageFiles.GetAllFiles(message);
        var files = await BuildAttachmentRowsAsync(message, attachments, transcription,
            file => MediaTranscription.DownloadAsync(tg, file, cancellationToken, local, message.ChatId, message.Id),
            cancellationToken);

        await AttachmentIndex.RecordAsync(tg.SessionDirectory, message.ChatId, message.Id, attachments);
        cancellationToken.ThrowIfCancellationRequested();
        var metrics = ToMetricsJson(message.InteractionInfo);

        return new JObject
        {
            ["schema"] = Name,
            ["schema_version"] = Version,
            ["chat_id"] = Wire.Id(message.ChatId),
            ["message_id"] = Wire.Id(message.Id),
            ["short_message_id"] = Wire.NullableId(ChatHistory.TryGetPublicPostId(message.Id)),
            ["tg_url"] = MessageLinks.GetTgUrl(message.ChatId, message.Id),
            ["source_chat_id"] = Wire.Id(item.SourceChatId),
            ["original_chat_id"] = Wire.Id(item.OriginalChatId),
            ["original_message_id"] = Wire.Id(item.OriginalMessageId),
            ["query"] = query,
            ["date"] = Wire.Timestamp(message.Date),
            ["edit_date"] = message.EditDate == 0 ? JValue.CreateNull() : Wire.Timestamp(message.EditDate),
            ["sender_type"] = sender.Type,
            ["sender_user_id"] = Wire.NullableId(sender.UserId),
            ["sender_chat_id"] = Wire.NullableId(sender.ChatId),
            ["sender_display_name"] = sender.DisplayName,
            ["sender_username"] = string.IsNullOrWhiteSpace(sender.Username) ? JValue.CreateNull() : sender.Username,
            ["kind"] = MessageFiles.GetKind(message.Content),
            ["text"] = MessageFiles.GetMessageText(message.Content),
            ["entities"] = ExtractEntities(message.Content),
            ["reply_to_chat_id"] = Wire.NullableId(replyChatId),
            ["reply_to_message_id"] = Wire.NullableId(replyMessageId),
            ["reply_target"] = replyPreview,
            ["forward_origin"] = Wire.NativeDetails(message.ForwardInfo),
            ["interaction_info"] = Wire.NativeDetails(message.InteractionInfo),
            ["interaction_info_available"] = metrics["interaction_info_available"],
            ["view_count"] = metrics["view_count"],
            ["forward_count"] = metrics["forward_count"],
            ["reply_count"] = metrics["reply_count"],
            ["reaction_counts"] = metrics["reaction_counts"],
            ["paid_reaction_count"] = metrics["paid_reaction_count"],
            ["has_comments"] = metrics["has_comments"],
            ["poll"] = message.Content is TdApi.MessageContent.MessagePoll poll ? Wire.NativeDetails(poll.Poll) : JValue.CreateNull(),
            ["service_event"] = ChatCommands.IsServiceMessage(message.Content) ? Wire.NativeDetails(message.Content) : JValue.CreateNull(),
            ["attachments"] = files,
            ["is_deleted"] = false,
            ["tombstone"] = JValue.CreateNull(),
            ["links"] = links is null ? JValue.CreateNull() : Wire.Link(links)
        };
    }

    public static JObject Tombstone(long chatId, long messageId, string? reason = null) => new()
    {
        ["schema"] = Name,
        ["schema_version"] = Version,
        ["chat_id"] = Wire.NullableId(chatId),
        ["message_id"] = Wire.NullableId(messageId),
        ["is_deleted"] = true,
        ["tombstone"] = new JObject { ["reason"] = reason ?? "missing_from_incremental_refresh" }
    };

    internal static JObject ToMetricsJson(TdApi.MessageInteractionInfo? interactionInfo)
    {
        var replies = interactionInfo?.ReplyInfo;
        var reactions = interactionInfo?.Reactions?.Reactions ?? [];
        return new JObject
        {
            ["interaction_info_available"] = interactionInfo is not null,
            ["view_count"] = interactionInfo?.ViewCount,
            ["forward_count"] = interactionInfo?.ForwardCount,
            ["reply_count"] = interactionInfo is null ? (long?)null : replies?.ReplyCount ?? 0,
            ["reaction_count"] = interactionInfo is null ? (long?)null : reactions.Sum(x => (long)x.TotalCount),
            ["reaction_counts"] = interactionInfo is null ? JValue.CreateNull() : BuildReactionCounts(reactions),
            ["paid_reaction_count"] = interactionInfo is null ? (long?)null
                : reactions.Where(x => x.Type is TdApi.ReactionType.ReactionTypePaid).Sum(x => (long)x.TotalCount),
            ["has_comments"] = interactionInfo is null ? (bool?)null : replies is not null
        };
    }

    internal static JArray BuildReactionCounts(IEnumerable<TdApi.MessageReaction> reactions)
    {
        var rows = new JArray();
        foreach (var reaction in reactions)
        {
            var (type, emoji, customEmojiId) = FormatReactionType(reaction.Type);
            rows.Add(new JObject
            {
                ["type"] = type,
                ["emoji"] = emoji is null ? JValue.CreateNull() : emoji,
                ["custom_emoji_id"] = Wire.NullableId(customEmojiId),
                ["count"] = (long)reaction.TotalCount,
                ["chosen"] = reaction.IsChosen
            });
        }

        return rows;
    }

    private static (string Type, string? Emoji, long? CustomEmojiId) FormatReactionType(TdApi.ReactionType? type) => type switch
    {
        TdApi.ReactionType.ReactionTypeEmoji emoji => ("emoji", emoji.Emoji, null),
        TdApi.ReactionType.ReactionTypeCustomEmoji customEmoji => ("custom_emoji", null, customEmoji.CustomEmojiId),
        TdApi.ReactionType.ReactionTypePaid => ("paid", null, null),
        _ => ("unknown", null, null)
    };

    private static async Task<(string Type, long? UserId, long? ChatId, string? DisplayName, string? Username)> ResolveSenderAsync(
        TelegramSession tg,
        TdApi.MessageSender? sender,
        CancellationToken cancellationToken)
    {
        try
        {
            if (sender is TdApi.MessageSender.MessageSenderUser userSender)
            {
                var user = await TelegramOperations.AwaitReadAsync(tg.Client.GetUserAsync(userSender.UserId),
                    cancellationToken, retainNativeOperation: true);
                var displayName = $"{user.FirstName} {user.LastName}".Trim();
                var username = user.Usernames?.ActiveUsernames?.FirstOrDefault();
                return ("user", userSender.UserId, null, displayName, username);
            }

            if (sender is TdApi.MessageSender.MessageSenderChat chatSender)
            {
                var chat = await TelegramOperations.AwaitReadAsync(tg.Client.GetChatAsync(chatSender.ChatId),
                    cancellationToken, retainNativeOperation: true);
                return ("chat", null, chatSender.ChatId, chat.Title,
                    await MessageLinks.TryGetUsernameAsync(tg, chatSender.ChatId, cancellationToken, retainNativeOperation: true));
            }
        }
        catch (TdException ex) when (ex.Error.Code is 400 or 403 or 404)
        {
            // Identity ids remain useful even when profile lookup is inaccessible.
        }

        return GetSenderIdentity(sender);
    }

    private static (string Type, long? UserId, long? ChatId, string? DisplayName, string? Username) GetSenderIdentity(
        TdApi.MessageSender? sender) =>
        sender switch
        {
            TdApi.MessageSender.MessageSenderUser user => ("user", user.UserId, null, null, null),
            TdApi.MessageSender.MessageSenderChat chat => ("chat", null, chat.ChatId, null, null),
            _ => ("unknown", null, null, null, null)
        };

    private static (long? ChatId, long? MessageId) GetReplyTarget(TdApi.Message message)
    {
        if (message.ReplyTo is not TdApi.MessageReplyTo.MessageReplyToMessage reply)
        {
            return (null, null);
        }

        return (reply.ChatId == 0 ? message.ChatId : reply.ChatId, reply.MessageId);
    }

    private static async Task<JToken> ResolveReplyPreviewAsync(
        TelegramSession tg, long? chatId, long? messageId, bool local, CancellationToken cancellationToken)
    {
        if (chatId is null || messageId is null or 0)
        {
            return JValue.CreateNull();
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var target = local
                ? await tg.Client.GetMessageLocallyAsync(chatId.Value, messageId.Value)
                : await tg.Client.GetMessageAsync(chatId.Value, messageId.Value);
            cancellationToken.ThrowIfCancellationRequested();
            if (target.ChatId != chatId || target.Id != messageId)
                throw new InvalidOperationException("Telegram returned a different reply target.");
            return new JObject
            {
                ["chat_id"] = Wire.Id(target.ChatId),
                ["message_id"] = Wire.Id(target.Id),
                ["date"] = Wire.Timestamp(target.Date),
                ["kind"] = MessageFiles.GetKind(target.Content),
                ["text"] = Truncate(MessageFiles.GetMessageText(target.Content), 240),
                ["resolved"] = true
            };
        }
        catch (TdException ex) when (ex.Error.Code is 400 or 403 or 404)
        {
            return new JObject
            {
                ["chat_id"] = Wire.NullableId(chatId),
                ["message_id"] = Wire.NullableId(messageId),
                ["resolved"] = false,
                ["error"] = local ? "reply_not_cached" : "reply_unavailable"
            };
        }
    }

    internal static async Task<JArray> BuildAttachmentRowsAsync(
        TdApi.Message message,
        IReadOnlyList<MessageFile> attachments,
        MediaTranscription? transcription,
        Func<MessageFile, Task<string>> download,
        CancellationToken cancellationToken)
    {
        var rows = new JArray();
        var caption = MessageFiles.GetCaption(message.Content);
        foreach (var file in attachments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var generatedText = await MediaTranscription.TranscribeAttachmentAsync(file, transcription, download, cancellationToken);
            rows.Add(ToAttachmentJson(file, caption, generatedText));
        }

        return rows;
    }

    internal static JObject ToAttachmentJson(MessageFile file, string caption, MediaTranscript? generatedText)
    {
        var local = file.File.Local;
        return new JObject
        {
            ["kind"] = file.Kind,
            ["role"] = file.Role,
            ["file_id"] = Wire.Id(file.FileId),
            ["remote_id"] = file.RemoteId,
            ["unique_id"] = file.UniqueId,
            ["file_name"] = file.FileName,
            ["mime_type"] = file.MimeType,
            ["width"] = file.Width,
            ["height"] = file.Height,
            ["duration"] = file.Duration,
            ["size"] = file.Size,
            ["property_path"] = file.PropertyPath,
            ["caption"] = caption,
            ["download_state"] = local is null ? "unknown" :
                local.IsDownloadingCompleted ? "downloaded" :
                local.IsDownloadingActive ? "downloading" :
                local.CanBeDownloaded ? "available" : "unavailable",
            ["local_path"] = local?.IsDownloadingCompleted == true ? local.Path : JValue.CreateNull(),
            ["generated_text"] = generatedText?.ToJson() ?? (JToken)JValue.CreateNull()
        };
    }

    private static JToken ExtractEntities(TdApi.MessageContent? content)
    {
        object? formattedText = content?.GetType().GetProperty("Text")?.GetValue(content)
            ?? content?.GetType().GetProperty("Caption")?.GetValue(content);
        return Wire.NativeDetails(formattedText?.GetType().GetProperty("Entities")?.GetValue(formattedText));
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max] + "…";
}
