#nullable enable

using System;
using System.IO;
using System.Threading.Tasks;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using System.Text.Json;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using Xunit;

namespace TgCli.Tests;

public sealed class SessionStorageTests : IDisposable
{
    private const string ValidConfig = """{"apiId":1,"apiHash":"synthetic"}""";
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"tgcli-session-test-{Guid.NewGuid():N}");

    [Theory]
    [InlineData("not JSON")]
    [InlineData("""{"ApiId":1}""")]
    [InlineData("""{"ApiId":0,"ApiHash":"synthetic"}""")]
    [InlineData("""{"ApiId":1,"apiId":2,"ApiHash":"synthetic"}""")]
    public void InvalidConfigPreservesExistingSession(string config)
    {
        var sentinel = CreateSentinel();
        Assert.Throws<ValidationException>(() => SessionSecret.Import(CreateSecret(config), _root, force: true));
        Assert.Equal("previous", File.ReadAllText(sentinel));
    }

    [Fact]
    public void OversizedArchiveEntryPreservesExistingSession()
    {
        var sentinel = CreateSentinel();
        var secret = CreateSecret(ValidConfig, archive =>
        {
            using var entry = archive.CreateEntry("tdlib-db/td.binlog", CompressionLevel.Fastest).Open();
            var block = new byte[1024 * 1024];
            for (var i = 0; i < 65; i++) entry.Write(block);
        });

        Assert.Throws<ValidationException>(() => SessionSecret.Import(secret, _root, force: true));
        Assert.Equal("previous", File.ReadAllText(sentinel));
    }

    [Fact]
    public void DuplicateArchiveEntryPreservesExistingSession()
    {
        var sentinel = CreateSentinel();
        var secret = CreateSecret(ValidConfig, archive =>
        {
            WriteEntry(archive, "tdlib-db/td.binlog", "synthetic");
            WriteEntry(archive, "tdlib-db/td.binlog", "duplicate");
        });

        Assert.Throws<ValidationException>(() => SessionSecret.Import(secret, _root, force: true));
        Assert.Equal("previous", File.ReadAllText(sentinel));
    }

    [Fact]
    public void ActiveSessionPreventsSecretExportBeforeReadingItsFiles()
    {
        Directory.CreateDirectory(_root);
        using var activeLease = new FileStream(Path.Combine(_root, "tdlib.lock"), FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None);

        Assert.ThrowsAny<IOException>(() => SessionSecret.Export(_root));
    }

    [Fact]
    public async Task FailedOwnerWriteImmediatelyReleasesExclusiveLock()
    {
        Directory.CreateDirectory(Path.Combine(_root, "tdlib.lock.owner"));

        var failure = await Record.ExceptionAsync(() =>
            TelegramSession.CreateAsync(_root, 1, "synthetic", saveConfig: false, noWait: true, cancellationToken: TestContext.Current.CancellationToken));
        Assert.True(failure is IOException or UnauthorizedAccessException, failure?.ToString());
        using var nextLease = new FileStream(Path.Combine(_root, "tdlib.lock"), FileMode.Open, FileAccess.ReadWrite,
            FileShare.None);
    }

    [Fact]
    public void OwnerMetadataForReusedPidIsStaleEvenWhileTheOsLockIsHeld()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "tdlib.lock.owner"),
            $$"""{"Pid":{{Environment.ProcessId}},"StartTimeUtcTicks":1}""");
        using var activeLease = new FileStream(Path.Combine(_root, "tdlib.lock"), FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None);

        var status = SessionLock.Inspect(_root);
        Assert.True(status.locked);
        Assert.True(status.owner_metadata_stale);
        Assert.Null(status.owner);
        Assert.Throws<IOException>(() => SessionLock.RepairStale(_root));
    }

    [Fact]
    public void PromotionFailureRestoresFilesAndReleasesTheOriginalCoordinationInode()
    {
        var sentinel = CreateSentinel();
        var lockPath = Path.Combine(_root, "tdlib.lock");
        File.WriteAllText(lockPath, "existing coordination inode");
        var configPath = Path.Combine(_root, "config.json");
        File.WriteAllText(configPath, "previous config");
        var db = Directory.CreateDirectory(Path.Combine(_root, "tdlib-db")).FullName;
        var cachePath = Path.Combine(db, "previous-cache");
        File.WriteAllText(cachePath, "previous cache");

        Assert.Throws<IOException>(() => SessionSecret.Import(CreateSecret(ValidConfig), _root, force: true,
            (source, destination) =>
            {
                if (Path.GetFileName(destination) == "tdlib-db") throw new IOException("Injected promotion failure");
                File.Move(source, destination);
            }));

        Assert.Equal("previous", File.ReadAllText(sentinel));
        Assert.Equal("previous config", File.ReadAllText(configPath));
        Assert.Equal("previous cache", File.ReadAllText(cachePath));
        Assert.Equal("existing coordination inode", File.ReadAllText(lockPath));
        using var next = new FileStream(lockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.Equal(4, Directory.GetFileSystemEntries(_root).Length);
    }

    [Fact]
    public async Task AnotherProcessCannotRepairAnActiveLeaseOrMissItsOwner()
    {
        var ct = TestContext.Current.CancellationToken;
        var sentinel = CreateSentinel();
        var lease = SessionLock.Acquire(_root);
        try
        {
            var status = await RunChildAsync("status", ct);
            Assert.Equal(0, status.ExitCode);
            using var json = JsonDocument.Parse(status.Output);
            Assert.True(json.RootElement.GetProperty("locked").GetBoolean());
            Assert.False(json.RootElement.GetProperty("owner_metadata_stale").GetBoolean());
            Assert.Equal(Environment.ProcessId.ToString(), json.RootElement.GetProperty("owner").GetString());

            var repair = await RunChildAsync("unlock", ct, "--stale-only");
            Assert.NotEqual(0, repair.ExitCode);
            Assert.Throws<IOException>(() => SessionSecret.Import(CreateSecret(ValidConfig), _root, force: true));
            Assert.True(File.Exists(sentinel));
        }
        finally { lease.Dispose(); }

        var after = await RunChildAsync("status", ct);
        using var afterJson = JsonDocument.Parse(after.Output);
        Assert.False(afterJson.RootElement.GetProperty("locked").GetBoolean());
        Assert.Equal(JsonValueKind.Null, afterJson.RootElement.GetProperty("owner").ValueKind);
    }

    [Fact]
    public void InterruptedPromotionIsRolledBackBeforeAnotherImport()
    {
        var sentinel = CreateSentinel();
        var transaction = Directory.CreateDirectory(Path.Combine(_root, ".tgcli-session-import")).FullName;
        var staged = Directory.CreateDirectory(Path.Combine(transaction, "new", "tdlib-db")).FullName;
        var backup = Directory.CreateDirectory(Path.Combine(transaction, "backup")).FullName;
        File.Move(sentinel, Path.Combine(backup, "old-session"));
        File.WriteAllText(Path.Combine(staged, "td.binlog"), "staged binlog");
        File.WriteAllText(Path.Combine(_root, "config.json"), "already promoted config");
        File.WriteAllText(Path.Combine(transaction, "promoting"), "");

        Assert.Throws<ValidationException>(() => SessionSecret.Import(CreateSecret(ValidConfig), _root, force: false));
        Assert.Equal("previous", File.ReadAllText(sentinel));
        Assert.False(File.Exists(Path.Combine(_root, "config.json")));
        Assert.False(Directory.Exists(transaction));
        Assert.False(SessionLock.Inspect(_root).locked);
    }

    private async Task<(int ExitCode, string Output)> RunChildAsync(string command, System.Threading.CancellationToken ct,
        params string[] additionalArguments)
    {
        var info = new ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var argument in new[] { typeof(TelegramSession).Assembly.Location, "session", command, "--session", _root })
            info.ArgumentList.Add(argument);
        foreach (var argument in additionalArguments) info.ArgumentList.Add(argument);
        using var child = Process.Start(info)!;
        var output = child.StandardOutput.ReadToEndAsync(ct);
        var error = child.StandardError.ReadToEndAsync(ct);
        await child.WaitForExitAsync(ct);
        return (child.ExitCode, child.ExitCode == 0 ? await output : await error);
    }

    private string CreateSentinel()
    {
        Directory.CreateDirectory(_root);
        var sentinel = Path.Combine(_root, "old-session");
        File.WriteAllText(sentinel, "previous");
        return sentinel;
    }

    private static string CreateSecret(string config, Action<ZipArchive>? addBinlog = null)
    {
        using var payload = new MemoryStream();
        using (var archive = new ZipArchive(payload, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteEntry(archive, "manifest.json", """{"Format":"tgcli.session","Version":1}""");
            WriteEntry(archive, "config.json", config);
            if (addBinlog is null) WriteEntry(archive, "tdlib-db/td.binlog", "synthetic");
            else addBinlog(archive);
        }
        return Convert.ToBase64String(payload.ToArray());
    }

    private static void WriteEntry(ZipArchive archive, string name, string text)
    {
        using var entry = archive.CreateEntry(name).Open();
        entry.Write(Encoding.UTF8.GetBytes(text));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}

[CollectionDefinition("Console prompts", DisableParallelization = true)]
public sealed class ConsolePromptsCollection;

[Collection("Console prompts")]
public sealed class SessionPromptTests
{
    [Fact]
    public void EofStopsPromptEvenWhenEmptyInputIsAllowed()
    {
        var prompt = typeof(TelegramSession).GetMethod("Prompt", BindingFlags.NonPublic | BindingFlags.Static)!;
        var originalInput = Console.In;
        var originalOutput = Console.Out;
        try
        {
            Console.SetIn(new EofThenThrow());
            Console.SetOut(new StringWriter());
            var error = Assert.Throws<TargetInvocationException>(() => prompt.Invoke(null, ["Last name", true]));
            Assert.Equal(TelegramAuthorizationFailure.LoginRequired,
                Assert.IsType<TelegramAuthorizationException>(error.InnerException).Reason);
        }
        finally
        {
            Console.SetIn(originalInput);
            Console.SetOut(originalOutput);
        }
    }

    private sealed class EofThenThrow : StringReader
    {
        private int _reads;

        internal EofThenThrow() : base("") { }

        public override string? ReadLine() =>
            _reads++ == 0 ? null : throw new InvalidOperationException("Prompt read past EOF.");
    }
}
