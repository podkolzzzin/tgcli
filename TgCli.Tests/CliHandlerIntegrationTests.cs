#nullable enable
using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Xunit;

namespace TgCli.Tests;

public sealed class CliHandlerIntegrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tgcli-handler-" + Guid.NewGuid().ToString("N"));
    private string SessionPath => Path.Combine(_root, "session");

    [Fact]
    public async Task DiagnosticsPrintsObservedLockWithoutOpeningTheSession()
    {
        Directory.CreateDirectory(SessionPath);
        using var held = SessionLock.Acquire(SessionPath);
        var (exitCode, stdout, _) = await InvokeAsync("diagnostics", "--no-wait");
        Assert.Equal(1, exitCode);
        var result = JObject.Parse(stdout);
        Assert.False(result.Value<bool>("healthy"));
        Assert.True(result["lock_status"]!.Value<bool>("locked"));
        Assert.Equal(JTokenType.Null, result["network_available"]!.Type);
        Assert.Equal("session_locked", result.Value<string>("error_code"));
    }

    [Fact]
    public async Task DiagnosticDeadlineStillReportsObservedLock()
    {
        Directory.CreateDirectory(SessionPath);
        using var held = SessionLock.Acquire(SessionPath);
        var (exitCode, stdout, _) = await InvokeAsync("diagnostics", "--lock-timeout", "30", "--request-timeout", "2");
        Assert.Equal(5, exitCode);
        var result = JObject.Parse(stdout);
        Assert.False(result.Value<bool>("healthy"));
        Assert.True(result["lock_status"]!.Value<bool>("locked"));
        Assert.Equal("request_timeout", result.Value<string>("error_code"));
    }

    [Theory]
    [InlineData("json")]
    [InlineData("jsonl")]
    [InlineData("plain")]
    public async Task EmptyProfileDiagnosticsPrintsOneLoginRequiredRecord(string format)
    {
        Directory.CreateDirectory(_root);
        var (exitCode, stdout, _) = await InvokeAsync("diagnostics", "--format", format);
        Assert.Equal(4, exitCode);
        var result = JObject.Parse(Assert.Single(stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries)));
        Assert.False(result.Value<bool>("healthy"));
        Assert.False(result["lock_status"]!.Value<bool>("locked"));
        Assert.False(result.Value<bool>("database_exists"));
        Assert.Equal("login_required", result.Value<string>("error_code"));
    }

    [Fact]
    public async Task DiagnosticHelpPrintsOnlyHelpAndLeavesSessionPathUntouched()
    {
        Directory.CreateDirectory(_root);
        var (exitCode, stdout, stderr) = await InvokeAsync("diagnostics", "--help");
        Assert.Equal(0, exitCode);
        Assert.Contains("--lock-timeout", stdout);
        Assert.DoesNotContain("\"healthy\"", stdout);
        Assert.Empty(stderr);
        Assert.False(Directory.Exists(SessionPath));
    }

    [Fact]
    public async Task UnsupportedDiagnosticFormatLeavesSessionPathUntouched()
    {
        Directory.CreateDirectory(_root);
        var (exitCode, stdout, _) = await InvokeAsync("diagnostics", "--format", "csv");
        Assert.Equal(2, exitCode);
        Assert.Empty(stdout);
        Assert.False(Directory.Exists(SessionPath));
    }

    [Fact]
    public async Task MissingSessionSecretUsesLoginRequired()
    {
        Directory.CreateDirectory(_root);
        var (exitCode, stdout, _) = await InvokeAsync("session", "export");
        Assert.Equal(4, exitCode);
        Assert.Empty(stdout);
        Assert.False(Directory.Exists(SessionPath));
    }

    [Fact]
    public async Task EmptyProfileReadUsesLoginRequired()
    {
        Directory.CreateDirectory(SessionPath);
        var (exitCode, stdout, _) = await InvokeAsync("chat", "list");
        Assert.Equal(4, exitCode);
        Assert.Empty(stdout);
        Assert.False(Directory.Exists(Path.Combine(SessionPath, "tdlib-db")));
    }

    [Theory]
    [InlineData("chat", "messages", "--chat-id", "1", "--schema", "simple")]
    [InlineData("chat", "export", "--chat-id", "1", "--all")]
    [InlineData("chat", "export", "--chat-id", "1", "--limit", "5", "--output", "snapshot.jsonl", "--format", "jsonl", "--incremental")]
    [InlineData("chat", "search", "--chat-id", "1", "--since-date", "2026-11-20T14:12:00")]
    [InlineData("link", "message", "--chat-id", "1", "--message-id", "2", "--format", "csv")]
    public async Task InvalidReadOptionsNeverOpenASession(params string[] args)
    {
        Directory.CreateDirectory(_root);
        var (exitCode, stdout, _) = await InvokeAsync(args);
        Assert.Equal(2, exitCode);
        Assert.Empty(stdout);
        Assert.False(Directory.Exists(SessionPath));
        Assert.False(File.Exists(Path.Combine(_root, "snapshot.jsonl")));
    }

    private async Task<(int ExitCode, string Stdout, string Stderr)> InvokeAsync(params string[] args)
    {
        var info = new ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            WorkingDirectory = _root
        };
        info.Environment.Remove("TGCLI_API_ID");
        info.Environment.Remove("TGCLI_API_HASH");
        info.Environment["XDG_CONFIG_HOME"] = _root;
        info.ArgumentList.Add(typeof(TgCommands).Assembly.Location);
        foreach (var arg in args) info.ArgumentList.Add(arg);
        info.ArgumentList.Add("--session");
        info.ArgumentList.Add(SessionPath);

        using var process = Process.Start(info)!;
        process.StandardInput.Close();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
        await process.WaitForExitAsync(timeout.Token);
        return (process.ExitCode, await stdout, await stderr);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
