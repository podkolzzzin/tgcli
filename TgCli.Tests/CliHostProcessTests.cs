using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using TgCli;
using Xunit;

public sealed class CliHostProcessTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"tgcli-host-{Guid.NewGuid():N}");
    private string Session => Path.Combine(_root, "session");

    public CliHostProcessTests() => Directory.CreateDirectory(_root);

    [Theory]
    [InlineData("chat", "mistyped")]
    [InlineData("mistyped")]
    [InlineData("chat", "messages")]
    [InlineData("chat", "messages", "--chat-id", "abc")]
    [InlineData("chat", "messages", "--chat-id", "-123", "--format", "csv")]
    [InlineData("chat", "messages", "--chat-id", "-123", "--limit", "0")]
    [InlineData("channel", "comments", "--chat-id", "-123", "--post-id", "42", "--all")]
    [InlineData("channel", "metrics", "--chat-id", "-123", "--since-date", "invalid")]
    [InlineData("chat", "search", "--chat-id", "-123", "--query", "needle", "--queries", "queries.txt")]
    [InlineData("bot", "token", "--username", "example_bot", "--revoke", "--format", "csv")]
    [InlineData("bot", "write", "--username", "example_bot", "--text", "--click")]
    [InlineData("diagnostics", "--request-timeout", "3601")]
    [InlineData("chat", "messages", "--chat-id", "-123", "--request-timeout=0")]
    [InlineData("session", "status", "--request-timeout", "1", "--request-timeout", "2")]
    [InlineData("transcribe")]
    [InlineData("transcribe", "--input", "--format")]
    [InlineData("mcp", "--request-timeout=1")]
    [InlineData("chat", "messages", "--chat-id=-123", "--local=false")]
    public async Task Invalid_input_fails_without_session_or_stdout(params string[] arguments)
    {
        using var process = Start(arguments, includeSession: arguments[0] != "transcribe");
        var result = await FinishAsync(process);

        Assert.Equal(2, result.Code);
        Assert.Empty(result.Output);
        Assert.NotEmpty(result.Error);
        Assert.DoesNotContain(" at TgCli.", result.Error);
        Assert.False(Directory.Exists(Session));
    }

    [Theory]
    [InlineData()]
    [InlineData("--version")]
    [InlineData("chat")]
    [InlineData("mcp", "--help")]
    [InlineData("bot", "write", "--help")]
    [InlineData("chat", "messages", "--chat-id=-123", "--help")]
    public async Task Help_and_version_are_session_free(params string[] arguments)
    {
        using var process = Start(arguments, includeSession: false);
        var result = await FinishAsync(process);

        Assert.Equal(0, result.Code);
        Assert.NotEmpty(result.Output);
        Assert.Empty(result.Error);
        Assert.False(Directory.Exists(Session));
    }

    [Fact]
    public async Task Global_timeout_bounds_real_lock_wait()
    {
        Directory.CreateDirectory(Session);
        using var lease = SessionLock.Acquire(Session);
        using var process = Start(["diagnostics", "--lock-timeout", "30", "--request-timeout", "1"]);
        var result = await FinishAsync(process);

        Assert.Equal(5, result.Code);
        Assert.Empty(result.Output);
        Assert.Contains("deadline", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.True(SessionLock.Inspect(Session).locked);
    }

    [Fact]
    public async Task Interrupt_while_waiting_for_lock_exits_130()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) return;

        Directory.CreateDirectory(Session);
        using var lease = SessionLock.Acquire(Session);
        using var process = Start(["diagnostics", "--lock-timeout", "30"]);
        await Task.Delay(600, TestContext.Current.CancellationToken);
        using var signal = Process.Start("kill", $"-INT {process.Id}")!;
        await signal.WaitForExitAsync(TestContext.Current.CancellationToken);
        Assert.Equal(0, signal.ExitCode);
        var result = await FinishAsync(process);

        Assert.Equal(130, result.Code);
        Assert.Empty(result.Output);
        Assert.Contains("canceled", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Closed_stdout_is_quiet()
    {
        using var process = Start(["session", "status"], closeOutput: true);
        var result = await FinishAsync(process, readOutput: false);

        Assert.Equal(130, result.Code);
        Assert.Empty(result.Error);
    }

    [Fact]
    public async Task Positional_installer_target_and_global_options_reach_the_handler()
    {
        // Unsupported targets are checked by the handler before it discovers any clients.
        using var process = Start(["mcp", "install", "unsupported-test-target", "--request-timeout=1", "--dry-run"]);
        var result = await FinishAsync(process);

        Assert.Equal(2, result.Code);
        Assert.Contains("MCP install target", result.Error);
        Assert.DoesNotContain("request-timeout", result.Error);
    }

    [Fact]
    public async Task Valid_formats_and_global_timeout_bind_to_the_session_status_handler()
    {
        using var process = Start(["--request-timeout=2", "session", "status", "--format=plain"]);
        var result = await FinishAsync(process);

        Assert.Equal(0, result.Code);
        Assert.Equal("unlocked", result.Output.Trim());
        Assert.Empty(result.Error);
        Assert.False(Directory.Exists(Session));
    }

    [Fact]
    public async Task Negative_chat_id_and_command_owned_timeout_reach_the_session_lock()
    {
        Directory.CreateDirectory(Session);
        using var lease = SessionLock.Acquire(Session);
        using var process = Start(["chat", "messages", "--chat-id=-123", "--request-timeout=10", "--no-wait"]);
        var result = await FinishAsync(process);

        Assert.Equal(1, result.Code);
        Assert.Empty(result.Output);
        Assert.Contains("locked", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    private Process Start(string[] args, bool includeSession = true, bool closeOutput = false)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = _root
        };
        start.Environment["HOME"] = _root;
        start.Environment["USERPROFILE"] = _root;
        start.Environment["XDG_CONFIG_HOME"] = _root;
        start.Environment["DOTNET_CLI_HOME"] = _root;
        start.Environment.Remove("TGCLI_API_ID");
        start.Environment.Remove("TGCLI_API_HASH");
        start.ArgumentList.Add(typeof(Program).Assembly.Location);
        foreach (var arg in args) start.ArgumentList.Add(arg);
        if (includeSession)
        {
            start.ArgumentList.Add("--session");
            start.ArgumentList.Add(Session);
        }
        var process = Process.Start(start)!;
        process.StandardInput.Close();
        if (closeOutput) process.StandardOutput.Close();
        return process;
    }

    private static async Task<(int Code, string Output, string Error)> FinishAsync(Process process, bool readOutput = true)
    {
        try
        {
            var output = readOutput ? process.StandardOutput.ReadToEndAsync() : Task.FromResult(string.Empty);
            var error = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(6));
            return (process.ExitCode, await output, await error);
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
