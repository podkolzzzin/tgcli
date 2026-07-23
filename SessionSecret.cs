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
        var secret = reader.ReadToEnd();
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
    private const string Format = "tgcli.session";
    private const int FormatVersion = 1;

    public static string Export(string? sessionDirectory)
    {
        var root = ResolveSessionDirectory(sessionDirectory);
        var configPath = Path.Combine(root, ConfigPath);
        var binlogPath = Path.Combine(root, "tdlib-db", "td.binlog");

        if (!File.Exists(configPath))
        {
            throw new ValidationException($"Session config was not found at {configPath}. Run `tgcli login` first.");
        }

        if (!File.Exists(binlogPath))
        {
            throw new ValidationException($"TDLib authorization state was not found at {binlogPath}. Run `tgcli login` first.");
        }

        EnsureSessionUnlocked(root);

        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteTextEntry(archive, ManifestPath, JsonSerializer.Serialize(new Manifest(
                Format,
                FormatVersion,
                DateTimeOffset.UtcNow,
                "Contains only config.json and tdlib-db/td.binlog. It does not contain message history, media, downloads, thumbnails, exports, attachment indexes, app binaries, or lock files.")));
            AddFile(archive, ConfigPath, configPath);
            AddFile(archive, BinlogPath, binlogPath);
        }

        return Convert.ToBase64String(output.ToArray());
    }

    public static void Import(string secret, string? sessionDirectory, bool force)
    {
        var root = ResolveSessionDirectory(sessionDirectory);
        var payload = Decode(secret);
        var files = ReadSecret(payload);
        ValidateImportTarget(root, force);
        EnsureSessionUnlocked(root);

        if (force && Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }

        Directory.CreateDirectory(Path.Combine(root, "tdlib-db"));
        File.WriteAllBytes(Path.Combine(root, ConfigPath), files[ConfigPath]);
        File.WriteAllBytes(Path.Combine(root, "tdlib-db", "td.binlog"), files[BinlogPath]);
        SetPrivatePermissions(root);
    }

    internal static Dictionary<string, byte[]> Inspect(string secret) => ReadSecret(Decode(secret));

    internal static string ResolveSessionDirectory(string? sessionDirectory)
    {
        if (!string.IsNullOrWhiteSpace(sessionDirectory))
        {
            return Path.GetFullPath(sessionDirectory);
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrWhiteSpace(home)
            ? Path.Combine(Environment.CurrentDirectory, ".tgcli")
            : Path.Combine(home, ".local", "share", "tgcli");
    }

    private static byte[] Decode(string secret)
    {
        var normalized = new string(secret.Where(c => !char.IsWhiteSpace(c)).ToArray());
        if (string.IsNullOrWhiteSpace(normalized))
        {
            throw new ValidationException("Session secret is empty.");
        }

        try
        {
            return Convert.FromBase64String(normalized);
        }
        catch (FormatException ex)
        {
            throw new ValidationException("Session secret is not valid base64.", ex);
        }
    }

    private static Dictionary<string, byte[]> ReadSecret(byte[] payload)
    {
        using var input = new MemoryStream(payload);
        using var archive = new ZipArchive(input, ZipArchiveMode.Read);
        var files = archive.Entries.ToDictionary(x => NormalizeEntryPath(x.FullName), ReadEntry, StringComparer.Ordinal);
        var allowed = new HashSet<string>([ManifestPath, ConfigPath, BinlogPath], StringComparer.Ordinal);
        var unexpected = files.Keys.Where(x => !allowed.Contains(x)).Order(StringComparer.Ordinal).ToArray();
        if (unexpected.Length > 0)
        {
            throw new ValidationException($"Session secret contains unexpected file(s): {string.Join(", ", unexpected)}.");
        }

        foreach (var required in allowed)
        {
            if (!files.ContainsKey(required))
            {
                throw new ValidationException($"Session secret is missing {required}.");
            }
        }

        var manifest = JsonSerializer.Deserialize<Manifest>(Encoding.UTF8.GetString(files[ManifestPath]));
        if (manifest?.Format != Format || manifest.Version != FormatVersion)
        {
            throw new ValidationException("Session secret has an unsupported format.");
        }

        return files;
    }

    private static void ValidateImportTarget(string root, bool force)
    {
        if (!Directory.Exists(root))
        {
            return;
        }

        var existing = Directory.EnumerateFileSystemEntries(root)
            .Select(Path.GetFileName)
            .Where(x => x is not null && x is not "tdlib.lock" and not "tdlib.lock.owner")
            .ToArray();
        if (existing.Length > 0 && !force)
        {
            throw new ValidationException($"Session directory {root} is not empty. Use --force to overwrite it.");
        }
    }

    private static void EnsureSessionUnlocked(string root)
    {
        var status = SessionLock.Inspect(root);
        if (status.locked)
        {
            throw new IOException(
                $"TDLib database is locked by another tgcli process (owner PID: {status.owner ?? "unknown"}). Stop that process before exporting or importing the session secret.");
        }
    }

    private static void AddFile(ZipArchive archive, string entryName, string path)
    {
        var entry = archive.CreateEntry(entryName, CompressionLevel.SmallestSize);
        using var entryStream = entry.Open();
        using var file = File.OpenRead(path);
        file.CopyTo(entryStream);
    }

    private static void WriteTextEntry(ZipArchive archive, string entryName, string value)
    {
        var entry = archive.CreateEntry(entryName, CompressionLevel.SmallestSize);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.Write(value);
    }

    private static byte[] ReadEntry(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        using var output = new MemoryStream();
        stream.CopyTo(output);
        return output.ToArray();
    }

    private static string NormalizeEntryPath(string value) => value.Replace('\\', '/').TrimStart('/');

    private static void SetPrivatePermissions(string root)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        TryChmod(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        TryChmod(Path.Combine(root, "tdlib-db"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        TryChmod(Path.Combine(root, ConfigPath), UnixFileMode.UserRead | UnixFileMode.UserWrite);
        TryChmod(Path.Combine(root, "tdlib-db", "td.binlog"), UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    private static void TryChmod(string path, UnixFileMode mode)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                return;
            }

            File.SetUnixFileMode(path, mode);
        }
        catch
        {
            // Permissions are best-effort on non-POSIX or restricted filesystems.
        }
    }

    private sealed record Manifest(string Format, int Version, DateTimeOffset CreatedAt, string Contents);
}

internal sealed record SessionLockStatus(
    string session_directory,
    bool locked,
    string? owner,
    bool owner_metadata_stale,
    string lock_path);

internal static class SessionLock
{
    public static SessionLockStatus Inspect(string? sessionDirectory)
    {
        var root = SessionSecret.ResolveSessionDirectory(sessionDirectory);
        var lockPath = Path.Combine(root, "tdlib.lock");
        var ownerPath = lockPath + ".owner";
        var owner = ReadOwner(ownerPath);
        var locked = false;

        if (File.Exists(lockPath))
        {
            try
            {
                using var stream = new FileStream(lockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException)
            {
                locked = true;
            }
        }

        return new SessionLockStatus(
            root,
            locked,
            owner,
            !locked && !string.IsNullOrWhiteSpace(owner),
            lockPath);
    }

    public static SessionLockStatus RepairStale(string? sessionDirectory)
    {
        var root = SessionSecret.ResolveSessionDirectory(sessionDirectory);
        if (!Directory.Exists(root))
        {
            return Inspect(root);
        }

        var lockPath = Path.Combine(root, "tdlib.lock");
        var ownerPath = lockPath + ".owner";
        try
        {
            using var stream = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            if (File.Exists(ownerPath))
            {
                File.Delete(ownerPath);
            }
        }
        catch (IOException ex)
        {
            var owner = ReadOwner(ownerPath);
            throw new IOException(
                $"Refusing to repair an active TDLib lock owned by PID {owner ?? "unknown"}.",
                ex);
        }

        return Inspect(root);
    }

    private static string? ReadOwner(string ownerPath)
    {
        try
        {
            if (!File.Exists(ownerPath))
            {
                return null;
            }

            var owner = File.ReadAllText(ownerPath).Trim();
            return owner.Length == 0 ? null : owner;
        }
        catch
        {
            return null;
        }
    }
}
