using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace TgCli.Tests;

public sealed class McpProcessRunnerTests
{
    [Theory]
    [InlineData("%PATH%")]
    [InlineData("!variable!")]
    [InlineData("embedded\"quote")]
    [InlineData("two\nlines")]
    [InlineData("embedded\0null")]
    public void CmdShimRejectsInputsItCannotRepresentSafely(string argument)
    {
        Assert.Throws<InvalidOperationException>(() =>
            McpProcessRunner.BuildWindowsCmdArguments(@"C:\test home\codex.cmd", [argument]));
    }

    [Fact]
    public void CmdShimKeepsArgumentBoundariesForNativeForwarding()
    {
        var actual = McpProcessRunner.BuildWindowsCmdArguments(
            @"C:\test home\codex.cmd", ["mcp", "", @"C:\sessions & media\"]);

        Assert.Equal("/d /v:off /s /c \"\"C:\\test home\\codex.cmd\" \"mcp\" \"\" \"C:\\sessions & media\\\\\"\"", actual);
    }

    [Fact]
    public async Task WindowsShimPassesLiteralArgumentsThroughANativeProcess()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var directory = new McpInstallerTests.TestDirectory();
        var script = Path.Combine(directory.Path, "capture.ps1");
        var batch = Path.Combine(directory.Path, "native wrapper.cmd");
        var capture = Path.Combine(directory.Path, "saved args.json");
        var ct = TestContext.Current.CancellationToken;
        await File.WriteAllTextAsync(script, """
            [System.IO.File]::WriteAllText($args[0], (ConvertTo-Json -InputObject @($args[1..2]) -Compress))
            """, ct);
        await File.WriteAllTextAsync(batch, "@echo off\r\n\"%SystemRoot%\\System32\\WindowsPowerShell\\v1.0\\powershell.exe\" -NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"%~dp0capture.ps1\" %*\r\n", ct);
        string[] arguments = [@"C:\sessions & media\", @"C:\shell ^ data (draft)"];

        var exit = await new McpProcessRunner().RunAsync(batch, [capture, ..arguments], ct);

        Assert.Equal(0, exit);
        Assert.Equal(arguments, JsonSerializer.Deserialize<string[]>(await File.ReadAllTextAsync(capture, ct)));
    }

    [Fact]
    public async Task UnixRunnerKeepsArgumentsLiteralAndClosesChildStdin()
    {
        if (OperatingSystem.IsWindows()) return;
        using var directory = new McpInstallerTests.TestDirectory();
        var capture = Path.Combine(directory.Path, "saved args");
        const string value = "some $(command) with spaces & a quote \"";
        var ct = TestContext.Current.CancellationToken;
        var exit = await new McpProcessRunner().RunAsync("/bin/sh",
            ["-c", """if read -r unused; then exit 42; fi; printf '%s' "$2" > "$1" """, "test", capture, value], ct);

        Assert.Equal(0, exit);
        Assert.Equal(value, await File.ReadAllTextAsync(capture, ct));
    }

    [Fact]
    public async Task CancellingNativeRegistrationStopsTheUnixChildProcess()
    {
        if (OperatingSystem.IsWindows()) return;
        using var directory = new McpInstallerTests.TestDirectory();
        var ready = Path.Combine(directory.Path, "ready");
        var leaked = Path.Combine(directory.Path, "leaked");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        var process = new McpProcessRunner().RunAsync("/bin/sh", [
            "-c",
            """
            /bin/sh -c 'printf ready > "$1"; sleep 2; printf leaked > "$2"' fixture "$1" "$2" &
            wait
            """, "fixture", ready, leaked
        ], cancel.Token);

        try
        {
            while (!File.Exists(ready)) await Task.Delay(20, deadline.Token);
            await cancel.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => process.WaitAsync(deadline.Token));
            await Task.Delay(TimeSpan.FromSeconds(2.1), TestContext.Current.CancellationToken);
            Assert.False(File.Exists(leaked));
        }
        finally { await cancel.CancelAsync(); }
    }
}
