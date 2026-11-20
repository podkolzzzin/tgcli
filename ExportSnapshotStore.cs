using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace TgCli;

internal sealed class ExportSnapshotStore : IDisposable
{
    private const int PersistenceVersion = 1;
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private static readonly JsonLoadSettings JsonSettings = new() { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error };
    private readonly string _path;
    private readonly string _manifestPath;
    private readonly string _workspace;
    private readonly string _pendingPath;
    private readonly FileStream _lease;
    private readonly Action<string, string> _replace;

    private ExportSnapshotStore(string path, string workspace, FileStream lease, Action<string, string>? replace)
    {
        _path = path;
        _manifestPath = path + ".manifest.json";
        _workspace = workspace;
        _pendingPath = Path.Combine(workspace, "pending.json");
        _lease = lease;
        _replace = replace ?? ((source, destination) => File.Move(source, destination, overwrite: true));
    }

    public static void ValidatePath(string path)
    {
        var name = Path.GetFileName(path);
        if (name.Length == 0 || name.EndsWith(".manifest.json", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(".tgcli", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Export destination must be a file path outside the reserved export sidecar names.");

        for (var parent = Path.GetDirectoryName(path); parent is not null; parent = Path.GetDirectoryName(parent))
        {
            if (Path.GetFileName(parent).EndsWith(".tgcli", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Export destination cannot be inside an export recovery directory.");
        }
    }

    public static ExportSnapshotStore Open(string path, Action<string, string>? replace = null)
    {
        path = Path.GetFullPath(path);
        ValidatePath(path);
        var workspace = path + ".tgcli";
        CheckFilePath(path);
        CheckFilePath(path + ".manifest.json");
        CheckLink(workspace);
        if (OperatingSystem.IsWindows())
            Directory.CreateDirectory(workspace);
        else
            Directory.CreateDirectory(workspace, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        FileStream lease;
        var lockPath = Path.Combine(workspace, "lock");
        CheckFilePath(lockPath);
        if (!File.Exists(lockPath) && Directory.EnumerateFileSystemEntries(workspace).Any())
            throw new IOException("The destination recovery directory already contains files without export ownership.");
        try
        {
            lease = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException ex)
        {
            throw new IOException("Could not acquire export destination ownership. Another writer may be using this destination.", ex);
        }

        var store = new ExportSnapshotStore(path, workspace, lease, replace);
        try
        {
            VerifyOwnership(lease, workspace, lockPath);
            store.Recover();
            store.DeleteOrphanStages();
            return store;
        }
        catch
        {
            store.Dispose();
            throw;
        }
    }

    public ExportCache? ReadCache(ExportRequest request, CancellationToken cancellationToken = default)
    {
        request.RequirePrincipal();
        if (!File.Exists(_path) && !File.Exists(_manifestPath))
        {
            if (request.CheckpointToken is not null)
                throw new InvalidDataException("Resuming with a checkpoint requires its existing export generation.");
            return null;
        }

        if (!File.Exists(_path) || !File.Exists(_manifestPath))
            throw new InvalidDataException("The export data and manifest must both exist. Create a fresh export.");

        var manifest = LoadObject(_manifestPath);
        if (!TryGetGeneration(manifest, out _) || manifest["persistence_version"]?.Type != JTokenType.Integer ||
            manifest.Value<int>("persistence_version") != PersistenceVersion ||
            manifest["resumable"]?.Type != JTokenType.Boolean || manifest.Value<bool>("resumable") != true ||
            !JToken.DeepEquals(manifest["snapshot_scope"], request.Scope) ||
            manifest.Value<string>("schema") != ExportSchema.Name || manifest.Value<string>("schema_version") != ExportSchema.Version)
            throw new InvalidDataException("The existing export has an incompatible account, chat, filter, projection or schema. Create a fresh export.");

        if (manifest["source_chats"] is not JArray sourceArray)
            throw new InvalidDataException("The export manifest has no source scope.");
        var sources = new HashSet<long>();
        foreach (var item in sourceArray)
        {
            if (!ExportRequest.TryParseId(item, out var source) || source == 0 || !sources.Add(source))
                throw new InvalidDataException("The export manifest has an invalid source scope.");
        }
        if (!sources.Contains(request.ChatId))
            throw new InvalidDataException("The export manifest source scope does not include the requested chat.");

        var checkpoint = manifest.Value<string>("resume_token");
        if (checkpoint is null || request.CheckpointToken is not null && request.CheckpointToken != checkpoint)
            throw new InvalidDataException("The checkpoint must match the current generation of the export being resumed.");

        using var input = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (manifest["output_byte_count"]?.Type != JTokenType.Integer || input.Length != manifest.Value<long>("output_byte_count") ||
            !string.Equals(Hash(input), manifest.Value<string>("output_sha256"), StringComparison.Ordinal))
            throw new InvalidDataException("Export data and manifest do not match. Create a fresh export from a verified source.");

        input.Position = 0;
        using var reader = new StreamReader(input, Utf8, detectEncodingFromByteOrderMarks: false);
        var rows = new Dictionary<ExportRowKey, CachedExportRow>();
        long offset = 0;
        var recordCount = 0;
        while (reader.ReadLine() is { } line)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var row = ParseObject(line);
                var key = ExportRowKey.Parse(row, sources);
                var byteCount = Utf8.GetByteCount(line);
                var deleted = row.Value<bool>("is_deleted");
                long date = 0;
                if (request.HasRowDate && !deleted)
                {
                    if (!DateTimeOffset.TryParse(row.Value<string>("date"), CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal, out var parsedDate))
                        throw new InvalidDataException("A dated export row has no valid date.");
                    date = parsedDate.ToUnixTimeSeconds();
                }

                // If the same stable ID appears again, only the final state survives the next write.
                rows[key] = new CachedExportRow(offset, byteCount, date, deleted);
                offset += byteCount + 1L;
                recordCount++;
            }
            catch (Exception ex) when (ex is JsonException or InvalidDataException or InvalidCastException)
            {
                throw new InvalidDataException($"Invalid export JSONL record at line {recordCount + 1}.", ex);
            }
        }

        if (input.Length != offset || manifest["output_record_count"]?.Type != JTokenType.Integer ||
            manifest.Value<int>("output_record_count") != recordCount)
            throw new InvalidDataException("Export manifest record count or JSONL line boundaries do not match the data.");

        var activeKeys = rows.Where(row => !row.Value.IsDeleted).Select(row => row.Key);
        if (checkpoint != request.CreateCheckpoint(sources, activeKeys, keepInputCursor: false))
            throw new InvalidDataException("The export checkpoint does not match its current message keys.");

        return new ExportCache(_path, (JObject)manifest["snapshot_scope"]!, sources, rows, checkpoint);
    }

    public async Task<JObject> CommitAsync(JObject metadata, Func<TextWriter, Task> write,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (IsLimitedView(metadata) && File.Exists(_manifestPath) && TryLoadObject(_manifestPath) is { } existing &&
            existing["complete"]?.Type == JTokenType.Boolean && existing.Value<bool>("complete") &&
            !IsLimitedView(existing) && MatchesCommittedData(existing))
        {
            throw new InvalidOperationException(
                "A limited export or checkpoint delta cannot replace a verified snapshot. Choose a separate output path.");
        }

        var generation = Guid.NewGuid();
        var (data, manifest, previous, journal) = Stages(generation);
        try
        {
            await using (var file = new FileStream(data, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                bufferSize: 4096, FileOptions.Asynchronous))
            {
                await using (var writer = new StreamWriter(file, Utf8, bufferSize: 4096, leaveOpen: true) { NewLine = "\n" })
                {
                    await write(writer);
                    await writer.FlushAsync(cancellationToken);
                }
                file.Flush(flushToDisk: true);
            }

            var published = (JObject)metadata.DeepClone();
            published.Remove("generation");
            published["persistence_version"] = PersistenceVersion;
            published["output_sha256"] = HashFile(data);
            published["output_byte_count"] = new FileInfo(data).Length;

            if (File.Exists(_manifestPath) && File.Exists(_path) && TryLoadObject(_manifestPath) is { } current &&
                TryGetGeneration(current, out _))
            {
                var currentGeneration = current.Value<string>("generation");
                current.Remove("generation");
                if (JToken.DeepEquals(current, published) && MatchesHash(_path, published.Value<string>("output_sha256")))
                {
                    current["generation"] = currentGeneration;
                    return current;
                }
            }

            published["generation"] = generation.ToString("N");
            WriteStage(manifest, published.ToString(Formatting.Indented));
            if (File.Exists(_manifestPath)) File.Copy(_manifestPath, previous);
            var commit = new JObject
            {
                ["generation"] = generation.ToString("N"),
                ["manifest_sha256"] = HashFile(manifest),
                ["data_sha256"] = published.Value<string>("output_sha256")
            };
            WriteStage(journal, commit.ToString(Formatting.None));
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(journal, _pendingPath);
            Publish(data, manifest);
            File.Delete(_pendingPath);
            return published;
        }
        finally
        {
            // Once the journal is visible its stages must survive until recovery completes.
            if (!File.Exists(_pendingPath))
            {
                TryDelete(data);
                TryDelete(manifest);
                TryDelete(previous);
                TryDelete(journal);
            }
        }
    }

    private void Recover()
    {
        if (!File.Exists(_pendingPath)) return;
        var commit = LoadObject(_pendingPath);
        if (!TryGetGeneration(commit, out var generation))
            throw new InvalidDataException("Export recovery journal has an invalid generation.");
        var (data, manifest, previous, _) = Stages(generation);
        var expectedManifestHash = commit.Value<string>("manifest_sha256");
        var expectedDataHash = commit.Value<string>("data_sha256");
        if (MatchesHash(File.Exists(manifest) ? manifest : _manifestPath, expectedManifestHash) &&
            MatchesHash(File.Exists(data) ? data : _path, expectedDataHash))
        {
            Publish(data, manifest);
        }
        else if (!IsCommittedManifest(_manifestPath))
        {
            if (!IsCommittedManifest(previous))
                throw new InvalidDataException("The pending export generation and its predecessor cannot be verified. Preserve the files for recovery.");
            _replace(previous, _manifestPath);
        }
        File.Delete(_pendingPath);
    }

    private bool IsCommittedManifest(string path) =>
        File.Exists(path) && TryLoadObject(path) is { } json && MatchesCommittedData(json);

    private bool MatchesCommittedData(JObject manifest) =>
        TryGetGeneration(manifest, out _) && File.Exists(_path) &&
        manifest["output_byte_count"]?.Type == JTokenType.Integer &&
        new FileInfo(_path).Length == manifest.Value<long>("output_byte_count") &&
        MatchesHash(_path, manifest.Value<string>("output_sha256"));

    private static bool IsLimitedView(JObject manifest) =>
        (manifest["complete"]?.Type == JTokenType.Boolean && !manifest.Value<bool>("complete")) ||
        manifest.Value<string>("snapshot_kind") is "partial" or "delta";

    private void Publish(string data, string manifest)
    {
        // The old data remains at its public path until the final rename. During the
        // interval readers can detect the mismatch using output_sha256 and the journal.
        if (File.Exists(manifest)) _replace(manifest, _manifestPath);
        if (File.Exists(data)) _replace(data, _path);
    }

    private (string Data, string Manifest, string Previous, string Journal) Stages(Guid generation) =>
        (Stage(generation, ".data"), Stage(generation, ".manifest"), Stage(generation, ".previous"), Stage(generation, ".ready"));

    private string Stage(Guid generation, string suffix) => Path.Combine(_workspace, generation.ToString("N") + suffix);

    private void DeleteOrphanStages()
    {
        foreach (var file in Directory.EnumerateFiles(_workspace))
        {
            if (Path.GetExtension(file) is ".data" or ".manifest" or ".previous" or ".ready" &&
                Guid.TryParseExact(Path.GetFileNameWithoutExtension(file), "N", out _))
                TryDelete(file);
        }
    }

    private static void VerifyOwnership(FileStream lease, string workspace, string lockPath)
    {
        ReadOnlySpan<byte> owner = "tgcli export workspace v1\n"u8;
        if (lease.Length == 0)
        {
            if (Directory.EnumerateFileSystemEntries(workspace).Any(path => path != lockPath))
                throw new IOException("The destination recovery directory already contains files without export ownership.");
            lease.Write(owner);
            lease.Flush(flushToDisk: true);
            return;
        }

        Span<byte> existing = stackalloc byte[owner.Length];
        if (lease.Length != owner.Length)
            throw new IOException("The destination recovery directory has an unrecognized owner.");
        lease.ReadExactly(existing);
        if (!existing.SequenceEqual(owner))
            throw new IOException("The destination recovery directory has an unrecognized owner.");
    }

    private static void WriteStage(string path, string content)
    {
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        file.Write(Utf8.GetBytes(content));
        file.Flush(flushToDisk: true);
    }

    private static bool TryGetGeneration(JObject json, out Guid generation) =>
        Guid.TryParseExact(json.Value<string>("generation"), "N", out generation);

    private static string HashFile(string path)
    {
        using var file = File.OpenRead(path);
        return Hash(file);
    }

    private static string Hash(Stream stream) => Convert.ToHexStringLower(SHA256.HashData(stream));

    private static bool MatchesHash(string path, string? expected) =>
        expected is { Length: 64 } && File.Exists(path) && HashFile(path) == expected;

    // Read without JSON.NET datetime coercion so persisted scope strings compare exactly.
    internal static JObject ParseObject(string json)
    {
        using var reader = new JsonTextReader(new StringReader(json)) { DateParseHandling = DateParseHandling.None };
        var value = JObject.Load(reader, JsonSettings);
        if (reader.Read()) throw new JsonReaderException("Trailing data in JSON object.");
        return value;
    }

    private static JObject LoadObject(string path) =>
        TryLoadObject(path) ?? throw new InvalidDataException("Export manifest or recovery journal contains invalid JSON.");

    private static JObject? TryLoadObject(string path)
    {
        try { return ParseObject(File.ReadAllText(path, Utf8)); }
        catch (Exception ex) when (ex is JsonException or DecoderFallbackException) { return null; }
    }

    private static void CheckFilePath(string path)
    {
        CheckLink(path);
        if (Directory.Exists(path)) throw new IOException("Export destination or sidecar path is a directory.");
    }

    private static void CheckLink(string path)
    {
        try
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Export destination, sidecars and recovery directory must not be symbolic links.");
        }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public void Dispose() => _lease.Dispose();
}
