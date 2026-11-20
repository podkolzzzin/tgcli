using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace TgCli;

internal sealed record SessionLockStatus(
    string session_directory,
    bool locked,
    string? owner,
    bool owner_metadata_stale,
    string lock_path);

internal static class SessionLock
{
    internal const string LockName = "tdlib.lock";
    internal const string OwnerName = "tdlib.lock.owner";

    public static SessionLockStatus Inspect(string? sessionDirectory)
    {
        var root = TelegramSession.ResolveSessionDirectory(sessionDirectory);
        var lockPath = Path.Combine(root, LockName);
        if (File.Exists(lockPath))
        {
            using var probe = TryOpen(lockPath, FileMode.Open, out _);
            return Status(root, locked: probe is null);
        }

        return Status(root, locked: false);
    }

    public static SessionLockStatus RepairStale(string? sessionDirectory)
    {
        var root = TelegramSession.ResolveSessionDirectory(sessionDirectory);
        if (!Directory.Exists(root)) return Status(root, locked: false);

        var lockPath = Path.Combine(root, LockName);
        using var probe = TryOpen(lockPath, FileMode.OpenOrCreate, out var contention);
        if (probe is null)
        {
            var owner = Status(root, locked: true).owner;
            throw new IOException($"Refusing to repair an active TDLib lock owned by PID {owner ?? "unknown"}.", contention);
        }

        File.Delete(Path.Combine(root, OwnerName));
        return Status(root, locked: false);
    }

    public static SessionLockLease Acquire(string root)
    {
        var lockPath = Path.Combine(root, LockName);
        var stream = TryOpen(lockPath, FileMode.OpenOrCreate, out var contention)
            ?? throw Busy(root, contention);
        return CreateLease(root, stream);
    }

    public static async Task<SessionLockLease> AcquireAsync(string root, TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        var elapsed = Stopwatch.StartNew();
        var lockPath = Path.Combine(root, LockName);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var stream = TryOpen(lockPath, FileMode.OpenOrCreate, out var contention);
            if (stream is not null) return CreateLease(root, stream);

            var remaining = timeout - elapsed.Elapsed;
            if (remaining <= TimeSpan.Zero) throw Busy(root, contention);
            await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(250, remaining.TotalMilliseconds)), cancellationToken);
        }
    }

    private static FileStream? TryOpen(string path, FileMode mode, out IOException? contention)
    {
        contention = null;
        try
        {
            var options = new FileStreamOptions { Mode = mode, Access = FileAccess.ReadWrite, Share = FileShare.None };
            if (!OperatingSystem.IsWindows() && mode is FileMode.OpenOrCreate)
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            return new FileStream(path, options);
        }
        catch (IOException ex) when (IsSharingViolation(ex))
        {
            contention = ex;
            return null;
        }
    }

    private static bool IsSharingViolation(IOException error)
    {
        var code = error.HResult & 0xffff;
        // FileStream reports EWOULDBLOCK (Linux/macOS) or the Windows sharing/lock violation.
        return OperatingSystem.IsWindows() ? code is 32 or 33 : code is 11 or 35;
    }

    private static SessionLockLease CreateLease(string root, FileStream stream)
    {
        var ownerPath = Path.Combine(root, OwnerName);
        var pendingPath = ownerPath + "." + Guid.NewGuid().ToString("N");
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var pending = new FileStream(pendingPath, options))
                JsonSerializer.Serialize(pending, new LockOwner(Environment.ProcessId, ProcessIdentity(Environment.ProcessId)));
            File.Move(pendingPath, ownerPath, overwrite: true);
            return new SessionLockLease(stream, ownerPath);
        }
        catch
        {
            stream.Dispose();
            TryDelete(pendingPath);
            throw;
        }
    }

    private static IOException Busy(string root, IOException? cause)
    {
        var owner = Status(root, locked: true).owner;
        return new IOException(
            $"TDLib database is locked by another tgcli process (owner PID: {owner ?? "unknown"}). Use --lock-timeout to wait longer or --no-wait to fail immediately.",
            cause);
    }

    private static SessionLockStatus Status(string root, bool locked)
    {
        var (owner, mismatched) = ReadOwner(Path.Combine(root, OwnerName));
        return new SessionLockStatus(root, locked, owner, mismatched || (!locked && owner is not null),
            Path.Combine(root, LockName));
    }

    private static (string? Owner, bool Mismatched) ReadOwner(string ownerPath)
    {
        try
        {
            var info = new FileInfo(ownerPath);
            if (!info.Exists) return (null, false);
            if (info.Length > 1024) return (null, true);
            var text = File.ReadAllText(ownerPath).Trim();
            if (text.Length == 0) return (null, false);
            if (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var legacyPid) && legacyPid > 0)
                return (text, true); // A legacy PID is a hint and carries no process identity.
            var metadata = JsonSerializer.Deserialize<LockOwner>(text);
            if (metadata?.Identity is null || ProcessIdentity(metadata.Pid) != metadata.Identity) return (null, true);
            return (metadata.Pid.ToString(CultureInfo.InvariantCulture), false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return (null, true);
        }
    }

    private static string? ProcessIdentity(int pid)
    {
        if (pid <= 0) return null;
        try
        {
            if (OperatingSystem.IsLinux())
            {
                var boot = File.ReadAllText("/proc/sys/kernel/random/boot_id").Trim();
                var stat = File.ReadAllText($"/proc/{pid}/stat");
                // Fields after the process name begin at field three. Start time is field 22.
                var fields = stat[(stat.LastIndexOf(')') + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                return fields.Length > 19 ? $"{boot}:{fields[19]}" : null;
            }

            using var process = Process.GetProcessById(pid);
            return process.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
            or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    internal static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private sealed record LockOwner(int Pid, string? Identity);
}

internal sealed class SessionLockLease(FileStream stream, string ownerPath) : IDisposable
{
    private int _disposed;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        // Remove only the diagnostic metadata while holding the coordination inode.
        try { SessionLock.TryDelete(ownerPath); }
        finally { stream.Dispose(); }
    }
}
