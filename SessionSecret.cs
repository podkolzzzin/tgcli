using System.Buffers.Binary;
using System.ComponentModel.DataAnnotations;
using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace TgCli;

public sealed class SessionCommands
{
    /// <summary>
    /// Export the current Telegram authorization state as a compact secret.
    /// </summary>
    /// <param name="session">Session directory. Defaults to ~/.local/share/tgcli.</param>
    public void Export(string? session = null)
    {
        Console.WriteLine(SessionSecret.Export(session));
    }

    /// <summary>
    /// Import a Telegram authorization secret from stdin.
    /// </summary>
    /// <param name="session">Session directory. Defaults to ~/.local/share/tgcli.</param>
    /// <param name="force">Overwrite an existing session.</param>
    public void Import(string? session = null, bool force = false)
    {
        using var reader = new StreamReader(Console.OpenStandardInput());
        var secret = SessionSecret.ReadInput(reader);
        SessionSecret.Import(secret, session, force);
    }

    /// <summary>
    /// Inspect the TDLib session lock without opening Telegram.
    /// </summary>
    public void Status(string format = "json", string? session = null)
    {
        var status = SessionLock.Inspect(session);
        switch (format.Trim().ToLowerInvariant())
        {
            case "json":
            case "jsonl":
                Console.WriteLine(JsonSerializer.Serialize(status));
                break;
            case "plain":
                Console.WriteLine(status.locked
                    ? $"locked owner={status.owner ?? "unknown"}"
                    : status.owner_metadata_stale
                        ? $"unlocked stale-owner={status.owner}"
                        : "unlocked");
                break;
            case "tsv":
                Console.WriteLine("session_directory\tlocked\towner\towner_metadata_stale\tlock_path");
                Console.WriteLine(string.Join('\t',
                    status.session_directory,
                    status.locked,
                    status.owner ?? string.Empty,
                    status.owner_metadata_stale,
                    status.lock_path));
                break;
            default:
                throw new ArgumentException("Format must be one of: json, jsonl, tsv, plain.", nameof(format));
        }
    }

    /// <summary>
    /// Remove stale lock-owner metadata. Refuses while any process owns the OS lock.
    /// </summary>
    public void Unlock(bool staleOnly = false, string? session = null)
    {
        if (!staleOnly)
        {
            throw new ValidationException("Pass --stale-only to confirm safe stale-lock repair.");
        }

        Console.WriteLine(JsonSerializer.Serialize(SessionLock.RepairStale(session)));
    }
}

internal static class SessionSecret
{
    public const string ManifestPath = "manifest.json";
    public const string ConfigPath = "config.json";
    public const string BinlogPath = "tdlib-db/td.binlog";
    internal const int MaxConfigBytes = 64 * 1024;
    private const int MaxPayloadBytes = 72 * 1024 * 1024;
    private const int MaxEncodedChars = (MaxPayloadBytes + 2) / 3 * 4;
    private const int MaxInputChars = MaxEncodedChars + 4 * 1024 * 1024;
    private const string Format = "tgcli.session";
    private const int FormatVersion = 1;
    private const string TransactionName = ".tgcli-session-import";
    private const string RetiredPrefix = ".tgcli-session-import-retired-";
    private static readonly uint[] ZipCrc32Table = CreateZipCrc32Table();

    public static string Export(string? sessionDirectory)
    {
        var root = TelegramSession.ResolveSessionDirectory(sessionDirectory);
        if (!Directory.Exists(root))
            throw new ValidationException($"Session directory {root} was not found. Run `tgcli login` first.");

        using var lease = SessionLock.Acquire(root);
        RecoverImport(root);
        var config = ReadFile(Path.Combine(root, ConfigPath), MaxConfigBytes);
        TelegramSession.ValidatePortableConfig(config);
        var binlog = ReadFile(Path.Combine(root, BinlogPath), EntryLimit(BinlogPath));

        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            var manifest = new Manifest(Format, FormatVersion, DateTimeOffset.UtcNow,
                "Contains only config.json and tdlib-db/td.binlog. It does not contain message history, media, downloads, thumbnails, exports, attachment indexes, app binaries, or lock files.");
            AddEntry(archive, ManifestPath, JsonSerializer.SerializeToUtf8Bytes(manifest));
            AddEntry(archive, ConfigPath, config);
            AddEntry(archive, BinlogPath, binlog);
        }

        return Convert.ToBase64String(output.GetBuffer(), 0, checked((int)output.Length));
    }

    public static void Import(string secret, string? sessionDirectory, bool force) =>
        Import(secret, sessionDirectory, force, MoveEntry);

    internal static void Import(string secret, string? sessionDirectory, bool force, Action<string, string> promote)
    {
        var files = Inspect(secret);
        var root = TelegramSession.ResolveSessionDirectory(sessionDirectory);
        TelegramSession.CreatePrivateSessionDirectory(root);
        using var lease = SessionLock.Acquire(root);
        RecoverImport(root);

        var existing = Directory.EnumerateFileSystemEntries(root)
            .Where(path => Path.GetFileName(path) is not (SessionLock.LockName or SessionLock.OwnerName))
            .Where(path => !Path.GetFileName(path).StartsWith(RetiredPrefix, StringComparison.Ordinal))
            .ToArray();
        if (existing.Length > 0 && !force)
            throw new ValidationException($"Session directory {root} is not empty. Use --force to overwrite it.");

        Install(root, existing, files, promote);
    }

    internal static string ReadInput(TextReader reader)
    {
        var secret = new StringBuilder();
        var buffer = new char[8192];
        int count;
        while ((count = reader.Read(buffer)) != 0)
        {
            if (secret.Length + count > MaxInputChars)
                throw new ValidationException("Session secret exceeds the size limit.");
            secret.Append(buffer, 0, count);
        }
        return secret.ToString();
    }

    internal static Dictionary<string, byte[]> Inspect(string secret)
    {
        var payload = Decode(secret);
        try
        {
            ValidateZipDirectory(payload);
            using var input = new MemoryStream(payload);
            using var archive = new ZipArchive(input, ZipArchiveMode.Read);
            if (archive.Entries.Count != 3)
                throw new ValidationException("Session secret must contain exactly three ZIP entries.");

            var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.Ordinal);
            foreach (var entry in archive.Entries)
            {
                var limit = EntryLimit(entry.FullName);
                if (limit == 0)
                    throw new ValidationException("Session secret contains an unexpected ZIP entry.");
                if (entry.Length <= 0 || entry.Length > limit)
                    throw new ValidationException($"Session secret entry {entry.FullName} has an invalid size.");
                if (!entries.TryAdd(entry.FullName, entry))
                    throw new ValidationException("Session secret contains a duplicate ZIP entry.");
            }

            var manifestBytes = ReadEntry(entries[ManifestPath]);
            var manifest = JsonSerializer.Deserialize<Manifest>(manifestBytes);
            if (manifest?.Format != Format || manifest.Version != FormatVersion)
                throw new ValidationException("Session secret has an unsupported format.");

            var config = ReadEntry(entries[ConfigPath]);
            TelegramSession.ValidatePortableConfig(config);
            return new Dictionary<string, byte[]>(StringComparer.Ordinal)
            {
                [ManifestPath] = manifestBytes,
                [ConfigPath] = config,
                [BinlogPath] = ReadEntry(entries[BinlogPath])
            };
        }
        catch (Exception ex) when (ex is InvalidDataException or JsonException or EndOfStreamException)
        {
            throw new ValidationException("Session secret is not a valid authorization archive.", ex);
        }
    }

    private static byte[] Decode(string secret)
    {
        if (secret.Length > MaxInputChars)
            throw new ValidationException("Session secret exceeds the size limit.");
        var encodedLength = 0;
        var hasOtherWhitespace = false;
        foreach (var character in secret)
        {
            if (!char.IsWhiteSpace(character)) encodedLength++;
            else if (character is not (' ' or '\t' or '\r' or '\n')) hasOtherWhitespace = true;
        }
        if (encodedLength == 0) throw new ValidationException("Session secret is empty.");
        if (encodedLength > MaxEncodedChars) throw new ValidationException("Session secret exceeds the size limit.");

        try
        {
            if (hasOtherWhitespace) secret = string.Concat(secret.Where(character => !char.IsWhiteSpace(character)));
            return Convert.FromBase64String(secret);
        }
        catch (FormatException ex)
        {
            throw new ValidationException("Session secret is not valid base64.", ex);
        }
    }

    private static void ValidateZipDirectory(ReadOnlySpan<byte> payload)
    {
        // Bound the directory before ZipArchive allocates entry objects. Our own exports use a plain, single-disk ZIP.
        const int footerLength = 22;
        var minimum = Math.Max(0, payload.Length - footerLength - ushort.MaxValue);
        for (var offset = payload.Length - footerLength; offset >= minimum; offset--)
        {
            var footer = payload[offset..];
            if (BinaryPrimitives.ReadUInt32LittleEndian(footer) != 0x06054b50
                || footer.Length != footerLength + BinaryPrimitives.ReadUInt16LittleEndian(footer[20..]))
                continue;

            var directorySize = BinaryPrimitives.ReadUInt32LittleEndian(footer[12..]);
            var directoryOffset = BinaryPrimitives.ReadUInt32LittleEndian(footer[16..]);
            if (BinaryPrimitives.ReadUInt32LittleEndian(footer[4..]) == 0
                && BinaryPrimitives.ReadUInt16LittleEndian(footer[8..]) == 3
                && BinaryPrimitives.ReadUInt16LittleEndian(footer[10..]) == 3
                && directorySize <= 4096 && (long)directorySize + directoryOffset == offset)
                return;
            break;
        }

        throw new ValidationException("Session secret must be a single ZIP archive with exactly three entries.");
    }

    private static int EntryLimit(string name) => name switch
    {
        ManifestPath => 16 * 1024,
        ConfigPath => MaxConfigBytes,
        BinlogPath => 64 * 1024 * 1024,
        _ => 0
    };

    private static byte[] ReadFile(string path, int maxBytes)
    {
        if (!File.Exists(path))
            throw new ValidationException($"Session authorization file was not found at {path}. Run `tgcli login` first.");
        using var file = File.OpenRead(path);
        if (file.Length <= 0 || file.Length > maxBytes)
            throw new ValidationException($"Session authorization file at {path} has an invalid size.");
        return ReadExact(file, (int)file.Length);
    }

    private static byte[] ReadEntry(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        var bytes = ReadExact(stream, checked((int)entry.Length));
        // ZipArchive does not verify the checksum when reading an entry.
        var crc = uint.MaxValue;
        foreach (var value in bytes)
            crc = ZipCrc32Table[(byte)(crc ^ value)] ^ (crc >> 8);
        if (~crc != entry.Crc32)
            throw new ValidationException($"Session secret entry {entry.FullName} has an invalid checksum.");
        return bytes;
    }

    private static uint[] CreateZipCrc32Table()
    {
        const uint polynomial = 0xedb88320; // Reflected IEEE CRC-32, as used by ZIP.
        var table = new uint[256];
        for (var index = 0; index < table.Length; index++)
        {
            var crc = (uint)index;
            for (var bit = 0; bit < 8; bit++)
                crc = (crc & 1) == 0 ? crc >> 1 : (crc >> 1) ^ polynomial;
            table[index] = crc;
        }
        return table;
    }

    private static byte[] ReadExact(Stream stream, int size)
    {
        var bytes = new byte[size];
        stream.ReadExactly(bytes);
        if (stream.ReadByte() != -1)
            throw new ValidationException("Session authorization data exceeded its declared size.");
        return bytes;
    }

    private static void AddEntry(ZipArchive archive, string name, byte[] content)
    {
        using var stream = archive.CreateEntry(name, CompressionLevel.SmallestSize).Open();
        stream.Write(content);
    }

    private static void Install(string root, string[] existing, Dictionary<string, byte[]> files,
        Action<string, string> promote)
    {
        var transaction = Path.Combine(root, TransactionName);
        var staged = Path.Combine(transaction, "new");
        var backup = Path.Combine(transaction, "backup");
        TelegramSession.CreatePrivateSessionDirectory(transaction);
        try
        {
            TelegramSession.CreatePrivateSessionDirectory(staged);
            TelegramSession.CreatePrivateSessionDirectory(backup);
            TelegramSession.CreatePrivateSessionDirectory(Path.Combine(staged, "tdlib-db"));
            WritePrivate(Path.Combine(staged, ConfigPath), files[ConfigPath]);
            WritePrivate(Path.Combine(staged, BinlogPath), files[BinlogPath]);

            foreach (var path in existing)
                MoveEntry(path, Path.Combine(backup, Path.GetFileName(path)));
            WriteMarker(transaction, "promoting");
            promote(Path.Combine(staged, ConfigPath), Path.Combine(root, ConfigPath));
            promote(Path.Combine(staged, "tdlib-db"), Path.Combine(root, "tdlib-db"));
            WriteMarker(transaction, "committed");
        }
        catch (Exception installError)
        {
            try { RecoverImport(root); }
            catch (Exception recoveryError)
            {
                throw new IOException(
                    $"Session replacement could not finish or roll back. The transaction was retained at {transaction}.",
                    new AggregateException(installError, recoveryError));
            }
            throw;
        }

        RecoverImport(root);
    }

    // All callers hold the session lease. The pathname of the coordination inode is never moved.
    internal static void RecoverImport(string root)
    {
        var transaction = Path.Combine(root, TransactionName);
        if (!Directory.Exists(transaction)) return;
        var staged = Path.Combine(transaction, "new");
        var backup = Path.Combine(transaction, "backup");
        if (!File.Exists(Path.Combine(transaction, "committed")))
        {
            if (File.Exists(Path.Combine(transaction, "promoting")))
            {
                foreach (var entry in new[] { ConfigPath, "tdlib-db" })
                {
                    var original = Path.Combine(staged, entry);
                    if (!File.Exists(original) && !Directory.Exists(original))
                        MoveEntry(Path.Combine(root, entry), original);
                }
            }
            if (Directory.Exists(backup))
                foreach (var existing in Directory.EnumerateFileSystemEntries(backup))
                    MoveEntry(existing, Path.Combine(root, Path.GetFileName(existing)));
        }

        var retired = Path.Combine(root, RetiredPrefix + Guid.NewGuid().ToString("N"));
        Directory.Move(transaction, retired);
        try { Directory.Delete(retired, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Could not remove private session import files at {retired}.");
        }
    }

    private static void MoveEntry(string source, string target)
    {
        if ((File.GetAttributes(source) & FileAttributes.Directory) != 0) Directory.Move(source, target);
        else File.Move(source, target);
    }

    private static void WriteMarker(string transaction, string name)
    {
        var pending = Path.Combine(transaction, name + ".pending");
        WritePrivate(pending, []);
        File.Move(pending, Path.Combine(transaction, name));
    }

    private static void WritePrivate(string path, byte[] content)
    {
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using var file = new FileStream(path, options);
        file.Write(content);
        file.Flush(flushToDisk: true);
    }

    private sealed record Manifest(string Format, int Version, DateTimeOffset CreatedAt, string Contents);
}
