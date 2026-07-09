using System.Diagnostics;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using TdLib;

namespace TgCli;

internal static class ExportSchema
{
    public const string Name = "tgcli.message";
    public const string Version = "4.0";

    public static async Task<JObject> BuildMessageAsync(
        TelegramSession tg,
        ExportedMessage item,
        bool includeLinks,
        string? transcribeCommand = null)
    {
        var message = item.Message;
        var sender = await ResolveSenderAsync(tg, message.SenderId);
        var (replyChatId, replyMessageId) = GetReplyTarget(message);
        var replyPreview = await ResolveReplyPreviewAsync(tg, replyChatId, replyMessageId);
        var links = includeLinks ? await MessageLinks.BuildAsync(tg, message.ChatId, message.Id) : null;
        var files = new JArray();

        foreach (var file in MessageFiles.GetAllFiles(message))
        {
            var generatedText = await TryTranscribeAsync(tg, file, transcribeCommand);
            files.Add(ToAttachmentJson(file, MessageFiles.GetMessageText(message.Content), generatedText));
        }

        await AttachmentIndex.RecordAsync(tg.SessionDirectory, message.ChatId, message.Id, MessageFiles.GetAllFiles(message));

        return new JObject
        {
            ["schema"] = Name,
            ["schema_version"] = Version,
            ["chat_id"] = message.ChatId,
            ["message_id"] = message.Id,
            ["source_chat_id"] = item.SourceChatId,
            ["date"] = FormatTimestamp(message.Date),
            ["edit_date"] = message.EditDate == 0 ? JValue.CreateNull() : FormatTimestamp(message.EditDate),
            ["sender_type"] = sender.Type,
            ["sender_user_id"] = sender.UserId is null ? JValue.CreateNull() : sender.UserId,
            ["sender_chat_id"] = sender.ChatId is null ? JValue.CreateNull() : sender.ChatId,
            ["sender_display_name"] = sender.DisplayName,
            ["sender_username"] = string.IsNullOrWhiteSpace(sender.Username) ? JValue.CreateNull() : sender.Username,
            ["kind"] = MessageFiles.GetKind(message.Content),
            ["text"] = MessageFiles.GetMessageText(message.Content),
            ["entities"] = ExtractEntities(message.Content),
            ["reply_to_chat_id"] = replyChatId is null ? JValue.CreateNull() : replyChatId,
            ["reply_to_message_id"] = replyMessageId is null ? JValue.CreateNull() : replyMessageId,
            ["reply_target"] = replyPreview,
            ["forward_origin"] = ToToken(message.ForwardInfo),
            ["reactions"] = ToToken(message.InteractionInfo),
            ["poll"] = message.Content is TdApi.MessageContent.MessagePoll poll ? ToToken(poll.Poll) : JValue.CreateNull(),
            ["service_event"] = IsService(message.Content) ? ToToken(message.Content) : JValue.CreateNull(),
            ["attachments"] = files,
            ["is_deleted"] = false,
            ["tombstone"] = JValue.CreateNull(),
            ["links"] = links is null ? JValue.CreateNull() : new JObject
            {
                ["short_message_id"] = links.ShortMessageId,
                ["tg_url"] = links.TgUrl,
                ["https_url"] = links.HttpsUrl,
                ["https_fallback"] = links.HttpsFallback
            }
        };
    }

    public static JObject Tombstone(long chatId, long messageId, string? reason = null) => new()
    {
        ["schema"] = Name,
        ["schema_version"] = Version,
        ["chat_id"] = chatId,
        ["message_id"] = messageId,
        ["is_deleted"] = true,
        ["tombstone"] = new JObject { ["reason"] = reason ?? "missing_from_incremental_refresh" }
    };

    private static async Task<(string Type, long? UserId, long? ChatId, string DisplayName, string? Username)> ResolveSenderAsync(
        TelegramSession tg,
        TdApi.MessageSender? sender)
    {
        try
        {
            if (sender is TdApi.MessageSender.MessageSenderUser userSender)
            {
                var user = await tg.Client.GetUserAsync(userSender.UserId);
                var displayName = $"{user.FirstName} {user.LastName}".Trim();
                var username = user.Usernames?.ActiveUsernames?.FirstOrDefault();
                return ("user", userSender.UserId, null, displayName, username);
            }

            if (sender is TdApi.MessageSender.MessageSenderChat chatSender)
            {
                var chat = await tg.Client.GetChatAsync(chatSender.ChatId);
                return ("chat", null, chatSender.ChatId, chat.Title, await MessageLinks.TryGetUsernameAsync(tg, chatSender.ChatId));
            }
        }
        catch
        {
            // Identity ids remain useful even when profile lookup is inaccessible.
        }

        return sender switch
        {
            TdApi.MessageSender.MessageSenderUser user => ("user", user.UserId, null, string.Empty, null),
            TdApi.MessageSender.MessageSenderChat chat => ("chat", null, chat.ChatId, string.Empty, null),
            _ => ("unknown", null, null, string.Empty, null)
        };
    }

    private static (long? ChatId, long? MessageId) GetReplyTarget(TdApi.Message message)
    {
        if (message.ReplyTo is not TdApi.MessageReplyTo.MessageReplyToMessage reply)
        {
            return (null, null);
        }

        var chatId = ChatHistory.GetLongProperty(reply, "ChatId");
        return (chatId is null or 0 ? message.ChatId : chatId, reply.MessageId);
    }

    private static async Task<JToken> ResolveReplyPreviewAsync(TelegramSession tg, long? chatId, long? messageId)
    {
        if (chatId is null || messageId is null or 0)
        {
            return JValue.CreateNull();
        }

        try
        {
            var target = await tg.Client.GetMessageAsync(chatId.Value, messageId.Value);
            return new JObject
            {
                ["chat_id"] = target.ChatId,
                ["message_id"] = target.Id,
                ["date"] = FormatTimestamp(target.Date),
                ["kind"] = MessageFiles.GetKind(target.Content),
                ["text"] = Truncate(MessageFiles.GetMessageText(target.Content), 240),
                ["resolved"] = true
            };
        }
        catch (Exception ex)
        {
            return new JObject
            {
                ["chat_id"] = chatId,
                ["message_id"] = messageId,
                ["resolved"] = false,
                ["error"] = ex.Message
            };
        }
    }

    private static JObject ToAttachmentJson(MessageFile file, string caption, string? generatedText)
    {
        var local = file.File.Local;
        return new JObject
        {
            ["kind"] = file.Kind,
            ["file_id"] = file.FileId,
            ["remote_id"] = file.RemoteId,
            ["unique_id"] = file.UniqueId,
            ["file_name"] = file.FileName,
            ["mime_type"] = file.MimeType,
            ["width"] = file.Width,
            ["height"] = file.Height,
            ["duration"] = file.Duration,
            ["size"] = file.Size,
            ["caption"] = caption,
            ["download_state"] = local is null ? "unknown" :
                local.IsDownloadingCompleted ? "downloaded" :
                local.IsDownloadingActive ? "downloading" :
                local.CanBeDownloaded ? "available" : "unavailable",
            ["local_path"] = local?.IsDownloadingCompleted == true ? local.Path : JValue.CreateNull(),
            ["generated_text"] = generatedText is null ? JValue.CreateNull() : new JObject
            {
                ["kind"] = "transcription",
                ["generated"] = true,
                ["text"] = generatedText
            }
        };
    }

    private static async Task<string?> TryTranscribeAsync(TelegramSession tg, MessageFile file, string? command)
    {
        if (string.IsNullOrWhiteSpace(command) || file.Kind is not ("voice" or "video-note"))
        {
            return null;
        }

        var downloaded = file.File.Local?.IsDownloadingCompleted == true
            ? file.File
            : await tg.Client.DownloadFileAsync(file.FileId, 32, 0, 0, synchronous: true);
        if (string.IsNullOrWhiteSpace(downloaded.Local?.Path))
        {
            return null;
        }

        var parts = command.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var info = new ProcessStartInfo(parts[0])
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        if (parts.Length == 2)
        {
            info.ArgumentList.Add(parts[1]);
        }
        info.ArgumentList.Add(downloaded.Local.Path);
        using var process = Process.Start(info) ?? throw new InvalidOperationException($"Unable to start transcription command '{parts[0]}'.");
        var output = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"Transcription command exited with code {process.ExitCode}: {await process.StandardError.ReadToEndAsync()}");
        }
        return output.Trim();
    }

    private static JToken ExtractEntities(TdApi.MessageContent? content)
    {
        object? formattedText = content?.GetType().GetProperty("Text")?.GetValue(content)
            ?? content?.GetType().GetProperty("Caption")?.GetValue(content);
        return ToToken(formattedText?.GetType().GetProperty("Entities")?.GetValue(formattedText));
    }

    private static bool IsService(TdApi.MessageContent? content) =>
        content is not null && content is not TdApi.MessageContent.MessageText
        && MessageFiles.GetAllFiles(new TdApi.Message { Content = content }).Count == 0
        && !content.GetType().Name.Contains("Poll", StringComparison.Ordinal);

    private static JToken ToToken(object? value) =>
        value is null ? JValue.CreateNull() : JToken.FromObject(value, JsonSerializer.CreateDefault());

    private static JValue FormatTimestamp(int unixSeconds) =>
        new(DateTimeOffset.FromUnixTimeSeconds(unixSeconds).ToString("O", System.Globalization.CultureInfo.InvariantCulture));

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max] + "…";
}
