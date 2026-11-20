using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace TgCli;

internal sealed record AttachmentIndexEntry(
    long chat_id,
    long message_id,
    int file_id,
    string kind,
    string? remote_id,
    string? unique_id = null,
    string? role = null);

internal static class AttachmentIndex
{
    private const string FileName = "attachment-index.jsonl";
    internal const int MaximumBytes = 1024 * 1024;
    private const int MaximumRowBytes = 4096;
    private static readonly SemaphoreSlim Gate = new(1);

    public static async Task RecordAsync(string sessionDirectory, long chatId, long messageId, IEnumerable<MessageFile> files)
    {
        if (string.IsNullOrWhiteSpace(sessionDirectory))
        {
            return;
        }

        var rows = files.Select(file => new AttachmentIndexEntry(
                chatId, messageId, file.FileId, file.Kind, file.RemoteId, file.UniqueId, file.Role))
            .Where(entry => entry.file_id > 0)
            .Select(Serialize)
            .Where(line => Encoding.UTF8.GetByteCount(line) <= MaximumRowBytes)
            .ToArray();
        if (rows.Length == 0)
        {
            return;
        }

        await Gate.WaitAsync();
        try
        {
            Directory.CreateDirectory(sessionDirectory);
            var path = Path.Combine(sessionDirectory, FileName);
            var info = new FileInfo(path);
            var rowBytes = rows.Sum(row => Encoding.UTF8.GetByteCount(row) + 1L);
            if ((info.Exists ? info.Length : 0) + rowBytes <= MaximumBytes)
            {
                await File.AppendAllTextAsync(path, string.Join('\n', rows) + "\n");
            }
            else
            {
                await CompactAsync(path, rows);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The index accelerates lookup. A cache I/O failure must leave message reads usable.
        }
        finally
        {
            Gate.Release();
        }
    }

    public static async Task<AttachmentIndexEntry?> ResolveAsync(string sessionDirectory, int fileId)
    {
        if (string.IsNullOrWhiteSpace(sessionDirectory))
        {
            return null;
        }

        await Gate.WaitAsync();
        try
        {
            var entries = await ReadRecentAsync(Path.Combine(sessionDirectory, FileName));
            return entries.LastOrDefault(entry => entry.file_id == fileId);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
        finally
        {
            Gate.Release();
        }
    }

    private static async Task CompactAsync(string path, IEnumerable<string> newRows)
    {
        var recent = (await ReadRecentAsync(path)).Concat(newRows.Select(Parse).OfType<AttachmentIndexEntry>());
        var rows = recent.Reverse().DistinctBy(entry => entry.file_id).Select(Serialize);
        var kept = new Stack<string>();
        var remaining = MaximumBytes / 2;
        foreach (var row in rows)
        {
            remaining -= Encoding.UTF8.GetByteCount(row) + 1;
            if (remaining < 0)
            {
                break;
            }

            kept.Push(row);
        }

        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllLinesAsync(temporary, kept, new UTF8Encoding(false));
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    private static async Task<IReadOnlyList<AttachmentIndexEntry>> ReadRecentAsync(string path)
    {
        if (!File.Exists(path))
        {
            return [];
        }

        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var length = (int)Math.Min(stream.Length, MaximumBytes);
        stream.Position = stream.Length - length;
        using var reader = new StreamReader(stream);
        if (stream.Position > 0)
        {
            await reader.ReadLineAsync();
        }

        var entries = new List<AttachmentIndexEntry>();
        while (await reader.ReadLineAsync() is { } line)
        {
            if (line.Length <= MaximumRowBytes && Parse(line) is { file_id: > 0 } entry)
            {
                entries.Add(entry);
            }
        }

        return entries;
    }

    private static string Serialize(AttachmentIndexEntry entry) =>
        JObject.FromObject(entry).ToString(Formatting.None);

    private static AttachmentIndexEntry? Parse(string line)
    {
        try
        {
            return JObject.Parse(line).ToObject<AttachmentIndexEntry>();
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
