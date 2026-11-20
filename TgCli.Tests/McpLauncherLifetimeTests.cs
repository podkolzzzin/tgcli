using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace TgCli.Tests;

public sealed class McpLauncherLifetimeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationPreventsLateWritesAfterLauncherExits(bool closePipes)
    {
        if (!OperatingSystem.IsLinux()) Assert.Skip("Requires Linux process groups and /proc.");

        using var fixture = new LauncherFixture(closePipes);
        using var sibling = Process.Start("/bin/sleep", "30")!;
        var testToken = TestContext.Current.CancellationToken;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(testToken);
        var operation = new McpProcessRunner().RunAsync(fixture.Executable, [], cancellation.Token);
        try
        {
            await fixture.WaitForLauncherExitAsync(testToken);

            await cancellation.CancelAsync();
            var error = await Record.ExceptionAsync(() => operation.WaitAsync(TimeSpan.FromSeconds(5), testToken));
            await fixture.ReleaseWriterAsync(testToken);

            Assert.False(File.Exists(fixture.ConfigPath), "The orphaned launcher wrote config after cancellation.");
            Assert.IsAssignableFrom<OperationCanceledException>(error);
            Assert.False(sibling.HasExited);
        }
        finally
        {
            cancellation.Cancel();
            if (!sibling.HasExited) sibling.Kill();
            await sibling.WaitForExitAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task SuccessfulReturnIncludesCompletionOfAnOrphanWithClosedPipes()
    {
        if (!OperatingSystem.IsLinux()) Assert.Skip("Requires Linux process groups and /proc.");

        using var fixture = new LauncherFixture(closePipes: true);
        var testToken = TestContext.Current.CancellationToken;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(testToken);
        cancellation.CancelAfter(TimeSpan.FromSeconds(10));
        var operation = new McpProcessRunner().RunAsync(fixture.Executable, [], cancellation.Token);
        try
        {
            await fixture.WaitForLauncherExitAsync(testToken);
            var completed = await Task.WhenAny(operation, Task.Delay(150, testToken));
            Assert.NotSame(operation, completed);

            await fixture.ReleaseWriterAsync(testToken);

            Assert.Equal(0, await operation.WaitAsync(TimeSpan.FromSeconds(5), testToken));
            Assert.True(File.Exists(fixture.ConfigPath));
        }
        finally
        {
            cancellation.Cancel();
        }
    }

    [Fact]
    public async Task CliDeadlinePreventsLateWriteAfterTheWholeHostExits()
    {
        if (!OperatingSystem.IsLinux()) Assert.Skip("Requires Linux process groups and /proc.");

        using var fixture = new LauncherFixture(closePipes: false);
        var testToken = TestContext.Current.CancellationToken;
        var info = new ProcessStartInfo(Path.ChangeExtension(typeof(McpProcessRunner).Assembly.Location, null))
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in new[] { "mcp", "install", "codex", "--request-timeout", "3" })
            info.ArgumentList.Add(argument);
        info.Environment["HOME"] = fixture.Home;
        info.Environment["XDG_CONFIG_HOME"] = fixture.Home;
        info.Environment["CODEX_HOME"] = Path.GetDirectoryName(fixture.ConfigPath);
        info.Environment["PATH"] = Path.GetDirectoryName(fixture.Executable);
        using var process = Process.Start(info)!;
        try
        {
            var output = process.StandardOutput.ReadToEndAsync(testToken);
            var error = process.StandardError.ReadToEndAsync(testToken);
            await fixture.WaitForLauncherExitAsync(testToken);
            await process.WaitForExitAsync(testToken).WaitAsync(TimeSpan.FromSeconds(10), testToken);
            await fixture.ReleaseWriterAsync(testToken);

            Assert.False(File.Exists(fixture.ConfigPath), "The launcher wrote config after tgcli returned a deadline error.");
            Assert.Equal(5, process.ExitCode);
            Assert.Equal("", await output.WaitAsync(TimeSpan.FromSeconds(5), testToken));
            Assert.Contains("deadline", await error.WaitAsync(TimeSpan.FromSeconds(5), testToken));
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
    }

    [Fact]
    public async Task LauncherTimeoutReportsUncertainOutcomeEvenWhenEntryWasWritten()
    {
        using var home = new McpInstallerTests.TestDirectory();
        var path = Path.Combine(home.Path, "config.toml");
        var target = new McpInstallTarget("Codex", path, McpConfigFormat.Toml, "mcp_servers", true, "fixture", McpClientKind.Codex);
        var launch = new McpLaunch("fixture-tgcli", ["mcp", "--session", "fixture-session"]);
        var expected = McpConfiguration.Merge(null, target.Format, target.RootKey, launch);
        var installer = new McpInstaller(new SaveAndTimeOutRunner(path, expected));

        var result = Assert.Single(await installer.InstallAsync([target], launch, false, TestContext.Current.CancellationToken));

        Assert.Equal("failed", result.Status);
        Assert.Contains("may have changed", result.Message);
        Assert.Equal(expected, File.ReadAllText(path));
    }

    private sealed class SaveAndTimeOutRunner(string path, string contents) : IMcpProcessRunner
    {
        public async Task<int> RunAsync(string executable, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
        {
            await File.WriteAllTextAsync(path, contents, cancellationToken);
            throw new OperationCanceledException();
        }
    }

    private sealed class LauncherFixture : IDisposable
    {
        private readonly McpInstallerTests.TestDirectory home = new();
        private readonly string pidFile;
        private readonly string release;
        private int writer;

        public LauncherFixture(bool closePipes)
        {
            var bin = Directory.CreateDirectory(Path.Combine(Home, "bin")).FullName;
            ConfigPath = Path.Combine(Directory.CreateDirectory(Path.Combine(Home, ".codex")).FullName, "config.toml");
            Executable = Path.Combine(bin, "codex");
            pidFile = Path.Combine(Home, "pids");
            release = Path.Combine(Home, "release");
            File.WriteAllText(Executable, $$"""
                #!/bin/sh
                (
                    {{(closePipes ? "exec </dev/null >/dev/null 2>&1" : "")}}
                    while [ ! -f '{{release}}' ]; do /bin/sleep .025; done
                    printf '# late native write\n' > '{{ConfigPath}}'
                ) &
                printf '%s %s\n' "$$" "$!" > '{{pidFile}}'
                exit 0
                """);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(Executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        public string Home => home.Path;
        public string ConfigPath { get; }
        public string Executable { get; }

        public async Task WaitForLauncherExitAsync(CancellationToken testToken)
        {
            int launcher = 0;
            await WaitUntilAsync(() =>
            {
                if (!File.Exists(pidFile)) return false;
                var pids = File.ReadAllText(pidFile).Trim().Split(' ');
                return pids.Length == 2 && int.TryParse(pids[0], out launcher) && int.TryParse(pids[1], out writer);
            }, testToken);
            await WaitUntilAsync(() => !IsRunning(launcher), testToken);
            Assert.True(IsRunning(writer), "The test writer must outlive its launcher.");
        }

        public async Task ReleaseWriterAsync(CancellationToken testToken)
        {
            await File.WriteAllTextAsync(release, "", testToken);
            await WaitUntilAsync(() => !IsRunning(writer), testToken);
        }

        public void Dispose()
        {
            if (writer > 0 && IsRunning(writer))
            {
                using var process = Process.GetProcessById(writer);
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
            }
            home.Dispose();
        }

        private static async Task WaitUntilAsync(Func<bool> ready, CancellationToken testToken)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(testToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            while (!ready()) await Task.Delay(20, timeout.Token);
        }

        private static bool IsRunning(int processId)
        {
            try
            {
                var state = File.ReadLines($"/proc/{processId}/status").FirstOrDefault(line => line.StartsWith("State:", StringComparison.Ordinal));
                return state is not null && !state["State:".Length..].TrimStart().StartsWith('Z');
            }
            catch (IOException) { return false; }
        }
    }
}
