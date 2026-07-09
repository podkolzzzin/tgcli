using Newtonsoft.Json.Linq;

namespace TgCli;

internal sealed record AttachmentIndexEntry(
    long chat_id,
    long message_id,
    int file_id,
    string kind,
    string? remote_id);

internal static class AttachmentIndex
{
    private const string FileName = "attachment-index.jsonl";

    public static async Task RecordAsync(string sessionDirectory, long chatId, long messageId, IEnumerable<MessageFile> files)
    {
        if (string.IsNullOrWhiteSpace(sessionDirectory))
        {
            return;
        }

        Directory.CreateDirectory(sessionDirectory);
        var path = Path.Combine(sessionDirectory, FileName);
        await using var stream = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read));
        foreach (var file in files)
        {
            var entry = new AttachmentIndexEntry(chatId, messageId, file.FileId, file.Kind, file.RemoteId);
            await stream.WriteLineAsync(JObject.FromObject(entry).ToString(Newtonsoft.Json.Formatting.None));
        }
    }

    public static async Task<AttachmentIndexEntry?> ResolveAsync(string sessionDirectory, int fileId)
    {
        if (string.IsNullOrWhiteSpace(sessionDirectory))
        {
            return null;
        }

        var path = Path.Combine(sessionDirectory, FileName);
        if (!File.Exists(path))
        {
            return null;
        }

        AttachmentIndexEntry? match = null;
        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                var value = JObject.Parse(line).ToObject<AttachmentIndexEntry>();
                if (value is not null && value.file_id == fileId)
                {
                    match = value;
                }
            }
            catch
            {
                // Ignore malformed cache rows; the cache is advisory only.
            }
        }

        return await Task.FromResult(match);
    }
}
