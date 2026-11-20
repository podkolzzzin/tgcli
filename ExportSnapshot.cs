using System.Buffers;
using System.Globalization;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace TgCli;

internal readonly record struct ExportRowKey(long ChatId, long MessageId)
{
    public static ExportRowKey Parse(JObject row, IReadOnlySet<long> sources)
    {
        if (row.Value<string>("schema") != ExportSchema.Name || row.Value<string>("schema_version") != ExportSchema.Version ||
            !ExportRequest.TryParseId(row["chat_id"], out var chatId) || !sources.Contains(chatId) ||
            !ExportRequest.TryParseId(row["message_id"], out var messageId) || messageId <= 0 ||
            (row.TryGetValue("source_chat_id", out var source) &&
             (!ExportRequest.TryParseId(source, out var sourceId) || sourceId != chatId)) ||
            row["is_deleted"]?.Type != JTokenType.Boolean)
            throw new InvalidDataException("Export JSONL row has an invalid schema, chat, message ID or deletion state.");

        return new ExportRowKey(chatId, messageId);
    }
}

internal sealed record CachedExportRow(long Offset, int ByteCount, long Date, bool IsDeleted);

internal sealed record ExportCache(
    string Path, JObject Scope, IReadOnlySet<long> Sources, IReadOnlyDictionary<ExportRowKey, CachedExportRow> Rows,
    string Checkpoint);

internal sealed class ExportSnapshot
{
    private readonly IReadOnlySet<long> _sources;
    private readonly ExportCache? _previous;
    private readonly IReadOnlyList<SnapshotRow> _rows;

    private sealed record SnapshotRow(
        ExportRowKey Key, long Date, bool IsDeleted, CachedExportRow? Cached = null, ExportedMessage? Message = null);

    private ExportSnapshot(JObject manifest, IReadOnlyList<ExportedMessage> messages, IReadOnlySet<long> sources,
        ExportCache? previous, IReadOnlyList<SnapshotRow> rows)
    {
        Manifest = manifest;
        Messages = messages;
        _sources = sources;
        _previous = previous;
        _rows = rows;
    }

    public JObject Manifest { get; }
    public IReadOnlyList<ExportedMessage> Messages { get; }

    public static ExportSnapshot Build(ExportRequest request, HistoryFetchResult history, ExportCache? previous)
    {
        request.RequirePrincipal();
        var sources = history.SourceChats.ToHashSet();
        if (sources.Count != history.SourceChats.Count || sources.Contains(0) || !sources.Contains(request.ChatId) ||
            !request.All && sources.Count != 1)
            throw new InvalidDataException("History traversal returned an invalid source scope.");
        if (previous is not null && (!JToken.DeepEquals(previous.Scope, request.Scope) || !sources.SetEquals(previous.Sources)))
            throw new InvalidDataException("The existing export belongs to a different history scope. Create a fresh export.");
        if (request.Checkpoint is not null && !sources.SetEquals(request.Checkpoint.Keys))
            throw new InvalidDataException("The checkpoint belongs to a different migration scope. Create a fresh export.");
        if (request.UpdatesSnapshot && request.CheckpointToken is not null && request.CheckpointToken != previous?.Checkpoint)
            throw new InvalidDataException("The checkpoint must match the current generation of the export being resumed.");

        var complete = request.All && !request.Local && history.Complete &&
            history.Gaps.Count == 0 && history.InaccessibleMessages == 0 &&
            sources.All(source => history.PagesByChat.GetValueOrDefault(source) > 0);
        var terminationReason = !history.Complete || complete ? history.TerminationReason :
            request.Local ? "local_cache_exhausted" : !request.All ? "single_page" : "coverage_unverified";
        if (request.RequireComplete && !complete)
            throw new InvalidOperationException(
                $"Export integrity check found incomplete history ({terminationReason}).");

        foreach (var row in history.Messages)
        {
            if (!sources.Contains(row.SourceChatId) || row.Message.ChatId != row.SourceChatId || row.Message.Id <= 0)
                throw new InvalidDataException("History traversal returned a message outside its source scope or without a valid ID.");
        }

        var messages = history.Messages.Where(request.Includes)
            .DistinctBy(item => new ExportRowKey(item.Message.ChatId, item.Message.Id)).ToArray();
        if (request.Local && messages.Length == 0)
            throw new InvalidOperationException("The local cache has no messages in the requested export scope.");
        var first = messages.Where(item => item.Message.Date > 0).MinBy(item => item.Message.Date);
        var last = messages.Where(item => item.Message.Date > 0).MaxBy(item => item.Message.Date);
        if (request.ExpectSince is not null && (first is null ||
            DateTimeOffset.FromUnixTimeSeconds(first.Message.Date) > request.ExpectSince))
            throw new InvalidOperationException(
                $"No fetched date reaches --expect-since={request.ExpectSince:O}.");
        if (messages.Length < request.ExpectCountMin)
            throw new InvalidOperationException(
                $"The fetched message count is below --expect-count-min={request.ExpectCountMin}.");

        var manifest = ChatHistory.BuildManifest(history);
        manifest["schema"] = ExportSchema.Name;
        manifest["schema_version"] = ExportSchema.Version;
        manifest["snapshot_scope"] = request.Scope.DeepClone();
        manifest["source_chats"] = new JArray(sources.Order().Select(ExportRequest.FormatId));
        manifest["source"] = request.Local ? "local_cache" : "server_history";
        manifest["complete"] = complete;
        manifest["message_count"] = messages.Length;
        manifest["first_date"] = DateValue(first);
        manifest["last_date"] = DateValue(last);
        manifest["termination_reason"] = terminationReason;

        var rows = request.HasRowIdentity ? Reconcile() : [];
        var resumable = complete && request.HasRowIdentity && (request.Checkpoint is null || previous is not null);
        var activeCount = request.HasRowIdentity ? rows.Count(row => !row.IsDeleted) : messages.Length;
        manifest["resumable"] = request.Path is not null && resumable;
        manifest["resume_token"] = complete && request.HasRowIdentity
            ? request.CreateCheckpoint(sources, rows.Where(row => !row.IsDeleted).Select(row => row.Key))
            : JValue.CreateNull();
        manifest["active_message_count"] = activeCount;
        manifest["tombstone_count"] = rows.Count - (request.HasRowIdentity ? activeCount : 0);
        manifest["output_record_count"] = request.HasRowIdentity ? rows.Count : messages.Length;

        return new ExportSnapshot(manifest, messages, sources, previous, rows);

        List<SnapshotRow> Reconcile()
        {
            var exactAuthoritativeRefresh = request.Incremental && !request.Local && complete && previous is not null &&
                request.Checkpoint is null && JToken.DeepEquals(previous.Scope, request.Scope) && sources.SetEquals(previous.Sources);
            var current = messages.ToDictionary(
                row => new ExportRowKey(row.Message.ChatId, row.Message.Id),
                row => row);
            var merged = new List<SnapshotRow>();
            foreach (var (key, cached) in previous?.Rows ?? new Dictionary<ExportRowKey, CachedExportRow>())
            {
                if (current.ContainsKey(key)) continue;
                merged.Add(exactAuthoritativeRefresh && !cached.IsDeleted
                    ? new SnapshotRow(key, 0, true)
                    : new SnapshotRow(key, cached.Date, cached.IsDeleted, cached));
            }
            merged.AddRange(current.Select(pair => new SnapshotRow(pair.Key,
                request.HasRowDate ? pair.Value.Message.Date : 0, false, Message: pair.Value)));

            return merged.OrderBy(row => row.IsDeleted).ThenBy(row => row.Date)
                .ThenBy(row => row.Key.ChatId).ThenBy(row => row.Key.MessageId).ToList();
        }
    }

    public async Task WriteAsync(TextWriter writer, Func<ExportedMessage, Task<JObject>> render,
        CancellationToken cancellationToken = default)
    {
        await using var source = _previous is null ? null : new FileStream(_previous.Path, FileMode.Open,
            FileAccess.Read, FileShare.Read, bufferSize: 4096, FileOptions.RandomAccess | FileOptions.Asynchronous);

        foreach (var row in _rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (row.Cached is not null)
            {
                await CopyCachedRowAsync(source!, writer, row.Cached, cancellationToken);
                continue;
            }

            var value = row.Message is null ? ExportSchema.Tombstone(row.Key.ChatId, row.Key.MessageId) : await render(row.Message);
            if (ExportRowKey.Parse(value, _sources) != row.Key || value.Value<bool>("is_deleted") != row.IsDeleted)
                throw new InvalidDataException("Rendered export row changed its source identity or deletion state.");
            await writer.WriteLineAsync(value.ToString(Formatting.None).AsMemory(), cancellationToken);
        }
    }

    private static async Task CopyCachedRowAsync(FileStream source, TextWriter writer, CachedExportRow row,
        CancellationToken cancellationToken)
    {
        var bytes = ArrayPool<byte>.Shared.Rent(row.ByteCount + 1);
        try
        {
            source.Position = row.Offset;
            await source.ReadExactlyAsync(bytes.AsMemory(0, row.ByteCount + 1), cancellationToken);
            if (bytes[row.ByteCount] != (byte)'\n')
                throw new InvalidDataException("Cached JSONL record has an invalid line boundary.");
            await writer.WriteLineAsync(Encoding.UTF8.GetString(bytes, 0, row.ByteCount).AsMemory(), cancellationToken);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(bytes);
        }
    }

    private static JValue DateValue(ExportedMessage? row) => row is null ? JValue.CreateNull() :
        new JValue(DateTimeOffset.FromUnixTimeSeconds(row.Message.Date).ToString("O", CultureInfo.InvariantCulture));
}
