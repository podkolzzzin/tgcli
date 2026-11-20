using System.Globalization;
using TdLib;

namespace TgCli;

internal sealed record DownloadSelection(TdApi.File File, long ChatId, long MessageId);

internal static class AttachmentDownloads
{
    public static AttachmentKind Validate(string? type, string? attachmentId, long chatId, long messageId)
    {
        if ((chatId == 0) != (messageId == 0))
            throw new ArgumentException("Provide both --chat-id and --message-id.");
        if (chatId == 0 && string.IsNullOrWhiteSpace(attachmentId))
            throw new ArgumentException("Provide a message identity or --attachment-id.");
        ParseNumericId(attachmentId);
        return AttachmentKinds.Parse(type ?? "file");
    }

    public static async Task<DownloadSelection> SelectAsync(
        TdApi.IClient client,
        string sessionDirectory,
        string? type,
        string? attachmentId,
        long chatId,
        long messageId,
        CancellationToken cancellationToken)
    {
        var kind = Validate(type, attachmentId, chatId, messageId);
        cancellationToken.ThrowIfCancellationRequested();
        attachmentId = string.IsNullOrWhiteSpace(attachmentId) ? null : attachmentId.Trim();
        var numericId = ParseNumericId(attachmentId);
        var indexed = numericId is { } id ? await AttachmentIndex.ResolveAsync(sessionDirectory, id) : null;

        if (chatId == 0 && indexed is { chat_id: not 0, message_id: not 0 })
            (chatId, messageId) = (indexed.chat_id, indexed.message_id);

        if (chatId != 0)
        {
            var message = await GetMessageAsync(client, chatId, messageId, cancellationToken);
            var files = MessageFiles.GetAllFiles(message);
            if (indexed is not null && (indexed.chat_id != chatId || indexed.message_id != messageId))
                indexed = null;

            var candidates = files.Where(file => AttachmentKinds.Matches(type, file.Kind));
            if (attachmentId is null)
            {
                candidates = candidates.Where(file => file.Role ==
                    (kind == AttachmentKind.Thumbnail ? "thumbnail" : "primary"));
            }
            else if (indexed is not null)
            {
                candidates = candidates.Where(file => Matches(
                    indexed.file_id, indexed.unique_id, indexed.remote_id, file.File)
                    && (indexed.role is null || file.Role == indexed.role));
            }
            else
            {
                candidates = candidates.Where(file => numericId.HasValue ? file.FileId == numericId : file.RemoteId == attachmentId);
            }

            var matches = candidates.Take(2).ToArray();
            if (matches.Length != 1)
            {
                throw new InvalidOperationException(matches.Length == 0
                    ? $"Message {chatId}/{messageId} has no matching attachment for '{attachmentId ?? type ?? "file"}'."
                    : $"Message {chatId}/{messageId} has multiple matching attachments. Specify the exact --attachment-id.");
            }

            return new(matches[0].File, chatId, messageId);
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (numericId.HasValue && kind is not (AttachmentKind.All or AttachmentKind.File))
        {
            throw new InvalidOperationException(
                $"Cannot verify the type of unindexed file id {numericId}. Supply its --chat-id and --message-id, or use a remote file id.");
        }

        TdApi.File resolved;
        try
        {
            resolved = numericId is { } fileId
                ? await AwaitNativeAsync(client.GetFileAsync(fileId), cancellationToken)
                : await AwaitNativeAsync(client.GetRemoteFileAsync(attachmentId!, AttachmentKinds.ToFileType(kind)), cancellationToken);
        }
        catch (TdException ex) when (IsStaleFile(ex))
        {
            throw new InvalidOperationException($"Attachment '{attachmentId}' is unavailable. Use --chat-id and --message-id to get the current attachment.", ex);
        }

        if (numericId.HasValue ? resolved.Id != numericId : resolved.Remote?.Id != attachmentId)
            throw new InvalidOperationException($"Telegram returned a different attachment for '{attachmentId}'.");
        return new(resolved, 0, 0);
    }

    public static async Task<TdApi.File> CompleteAsync(
        TdApi.IClient client,
        TdApi.File file,
        string? type,
        long chatId,
        long messageId,
        CancellationToken cancellationToken)
    {
        try
        {
            return await DownloadAsync(client, file, cancellationToken);
        }
        catch (TdException ex) when (chatId != 0 && messageId != 0 && IsStaleFile(ex))
        {
            var message = await GetMessageAsync(client, chatId, messageId, cancellationToken);
            var refreshed = MessageFiles.GetAllFiles(message).FirstOrDefault(candidate =>
                AttachmentKinds.Matches(type, candidate.Kind)
                && Matches(file.Id, file.Remote?.UniqueId, file.Remote?.Id, candidate.File));
            if (refreshed is null)
                throw new InvalidOperationException($"Message {chatId}/{messageId} no longer contains file id {file.Id}.", ex);

            return await DownloadAsync(client, refreshed.File, cancellationToken);
        }
    }

    private static async Task<TdApi.File> DownloadAsync(TdApi.IClient client, TdApi.File file, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (AtomicDownload.TryGetCompletedPath(file) is not null)
            return file;

        var downloaded = await AwaitNativeAsync(
            client.DownloadFileAsync(file.Id, priority: 32, offset: 0, limit: 0, synchronous: true),
            cancellationToken);
        if (downloaded.Id != file.Id || !Matches(file.Id, file.Remote?.UniqueId, file.Remote?.Id, downloaded))
            throw new IOException($"Telegram returned a different file while downloading file id {file.Id}.");
        AtomicDownload.GetCompletedPath(downloaded);
        return downloaded;
    }

    private static async Task<TdApi.Message> GetMessageAsync(
        TdApi.IClient client, long chatId, long messageId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var message = await AwaitNativeAsync(client.GetMessageAsync(chatId, messageId), cancellationToken);
        if (message.ChatId != chatId || message.Id != messageId)
            throw new InvalidOperationException($"Telegram returned a different message for {chatId}/{messageId}.");
        return message;
    }

    private static bool Matches(int id, string? uniqueId, string? remoteId, TdApi.File file)
    {
        if (!string.IsNullOrWhiteSpace(uniqueId))
            return file.Remote?.UniqueId == uniqueId;
        if (!string.IsNullOrWhiteSpace(remoteId))
            return file.Remote?.Id == remoteId;
        return file.Id == id;
    }

    private static int? ParseNumericId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var text = value.AsSpan().Trim();
        var digits = text[0] is '-' or '+' ? text[1..] : text;
        if (digits.IsEmpty || digits.IndexOfAnyExceptInRange('0', '9') >= 0)
            return null;
        if (!int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var id) || id <= 0)
            throw new ArgumentException("A numeric attachment id must be a positive 32-bit integer.");
        return id;
    }

    private static bool IsStaleFile(TdException error)
    {
        var message = error.Error.Message ?? string.Empty;
        return error.Error.Code is 400 or 404
            && (message.Contains("FILE_REFERENCE", StringComparison.OrdinalIgnoreCase)
                || message.Contains("FILE_ID_INVALID", StringComparison.OrdinalIgnoreCase)
                || message.Equals("File not found", StringComparison.OrdinalIgnoreCase)
                || message.Equals("Wrong file identifier", StringComparison.OrdinalIgnoreCase)
                || message.Equals("Invalid file ID", StringComparison.OrdinalIgnoreCase));
    }

    private static async Task<T> AwaitNativeAsync<T>(Task<T> pending, CancellationToken cancellationToken)
    {
        // The operation owner may stop waiting, but holds its lease until the native request drains.
        var result = await pending;
        cancellationToken.ThrowIfCancellationRequested();
        return result;
    }
}
