using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace TgCli.Tests;

public sealed class McpInstallerRegressionTests
{
    private static readonly McpLaunch Launch = new("/test/tgcli", ["mcp", "--session", "/test/custom session"]);

    [Theory]
    [InlineData("\n", "mcp ")]
    [InlineData("\r", "\tmc\\u0070\t:\f")]
    [InlineData("\r\n", "m\\\r\n cp\t")]
    public async Task EclipseConflictIsDetectedWithoutMakingAWrite(string newline, string prefix)
    {
        using var home = new McpInstallerTests.TestDirectory();
        var path = Path.Combine(home.Path, "conflict.prefs");
        var original = "other=unaltered" + newline + prefix + """{"servers":{"tgcli":{"command":"different","args":[]},"keep":{"command":"other"}}}""" + newline;
        await File.WriteAllTextAsync(path, original, TestContext.Current.CancellationToken);
        var target = new McpInstallTarget("Eclipse", path, McpConfigFormat.EclipsePreferences, "servers", true);

        var result = Assert.Single(await new McpInstaller(new NoRunner()).InstallAsync([target], Launch, false, TestContext.Current.CancellationToken));

        Assert.Equal("failed", result.Status);
        Assert.Contains("different tgcli MCP entry", result.Message);
        Assert.Equal(original, await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
        Assert.Single(Directory.GetFiles(home.Path));
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r")]
    [InlineData("\r\n")]
    public async Task EclipseInstallPreservesUnrelatedServersAndPhysicalLines(string newline)
    {
        using var home = new McpInstallerTests.TestDirectory();
        var path = Path.Combine(home.Path, "prefs");
        var leading = "# retained comment\\ " + newline + "\tother\\ key : value\\z" + newline;
        var trailing = "last=  keep  " + newline + newline;
        var original = leading + "mcp \f= {\\\r\n \t\"servers\":{\"keep\":{\"command\":\"existing\"}}}" + newline + trailing;
        var ct = TestContext.Current.CancellationToken;
        await File.WriteAllTextAsync(path, original, ct);
        var target = new McpInstallTarget("Eclipse", path, McpConfigFormat.EclipsePreferences, "servers", true);
        var installer = new McpInstaller(new NoRunner());

        Assert.Equal("would-install", Assert.Single(await installer.InstallAsync([target], Launch, true, ct)).Status);
        Assert.Equal(original, await File.ReadAllTextAsync(path, ct));
        Assert.Single(Directory.GetFiles(home.Path));

        Assert.Equal("installed", Assert.Single(await installer.InstallAsync([target], Launch, false, ct)).Status);
        var saved = await File.ReadAllTextAsync(path, ct);
        Assert.StartsWith(leading, saved);
        Assert.EndsWith(trailing, saved);
        // The replacement is a single property. Check its actual JSON, including the old server.
        var propertyLine = saved[leading.Length..^trailing.Length];
        Assert.StartsWith("mcp=", propertyLine);
        Assert.EndsWith(newline, propertyLine);
        var json = propertyLine[4..^newline.Length].Replace("\\n", "\n").Replace("\\:", ":");
        var servers = JsonNode.Parse(json)!["servers"]!;
        Assert.Equal("existing", servers["keep"]!["command"]!.GetValue<string>());
        Assert.Equal("/test/custom session", servers["tgcli"]!["args"]![2]!.GetValue<string>());
        Assert.Equal(original, await File.ReadAllTextAsync(Assert.Single(Directory.GetFiles(home.Path, "*.tgcli-backup-*")), ct));

        Assert.Equal("already-installed", Assert.Single(await installer.InstallAsync([target], Launch, false, ct)).Status);
        Assert.Equal(saved, await File.ReadAllTextAsync(path, ct));
        Assert.Single(Directory.GetFiles(home.Path, "*.tgcli-backup-*"));
    }

    // Fixtures follow the documented java.util.Properties.load(Reader) syntax.
    [Theory]
    [InlineData("  a\\ b\\:c\\=d\t : \fvalue\\z\\t\\u0041 ", "a b:c=d", "valuez\tA ")]
    [InlineData("unrelated=x\rcheeses\r", "cheeses", "")]
    [InlineData("m\\\r cp  abc\\\r  !def", "mcp", "abc!def")]
    [InlineData("! comment with trailing backslash \\\nmcp:abc\\\\\nfoo=x", "mcp", "abc\\")]
    [InlineData("foo:one\\\n\t\nmcp\f=two", "mcp", "two")]
    [InlineData("fruits  apple, banana, pear, \\\n  cantaloupe, watermelon, \\\n  kiwi, mango", "fruits", "apple, banana, pear, cantaloupe, watermelon, kiwi, mango")]
    public void JavaPropertiesReadReferenceSyntax(string file, string key, string expected) =>
        Assert.Equal(expected, JavaProperties.Get(file, key));

    [Theory]
    [InlineData("RECENT_WORKSPACES=/test/work\rRECENT_WORKSPACES /test/other")]
    [InlineData("RECENT_WORKSPACES=\\u0 41")]
    [InlineData("unrelated=\\u0g41\nRECENT_WORKSPACES=/test/work")]
    public async Task MalformedEclipseRegistryDoesNotBlockOtherTargets(string registry)
    {
        using var home = new McpInstallerTests.TestDirectory();
        Directory.CreateDirectory(Path.Combine(home.Path, ".copilot"));
        var eclipse = Directory.CreateDirectory(Path.Combine(home.Path, ".eclipse"));
        var path = Path.Combine(eclipse.FullName, "org.eclipse.ui.ide.prefs");
        var ct = TestContext.Current.CancellationToken;
        await File.WriteAllTextAsync(path, registry, ct);
        var environment = new McpInstallEnvironment(home.Path, "linux", new Dictionary<string, string>());

        var results = await new McpInstaller(new NoRunner()).InstallAsync(McpClientDiscovery.Find("copilot", environment), Launch, false, ct);

        Assert.Contains(results, r => r.Client.StartsWith("Copilot CLI") && r.Status == "installed");
        Assert.Contains(results, r => r.Client.StartsWith("Eclipse Copilot") && r.Status == "failed");
        Assert.Equal(registry, await File.ReadAllTextAsync(path, ct));
    }

    [Fact]
    public void UnixPathSkipsNonExecutableCandidates()
    {
        if (OperatingSystem.IsWindows()) return;
        using var home = new McpInstallerTests.TestDirectory();
        var first = Directory.CreateDirectory(Path.Combine(home.Path, "first")).FullName;
        var second = Directory.CreateDirectory(Path.Combine(home.Path, "second")).FullName;
        var shadow = Path.Combine(first, "codex");
        var executable = Path.Combine(second, "codex");
        File.WriteAllText(shadow, "fixture only");
        File.WriteAllText(executable, "fixture only");
        File.SetUnixFileMode(shadow, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        var environment = new McpInstallEnvironment(home.Path, "linux", new Dictionary<string, string> { ["PATH"] = first + ":" + second });

        Assert.Equal(executable, McpClientDiscovery.Find("codex", environment).Single().Cli);
        File.SetUnixFileMode(executable, UnixFileMode.UserRead);
        Assert.Null(environment.Executable("codex"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AnotherInstallerCannotWriteWhileNativeRegistrationOwnsTheConfig(bool useNative)
    {
        using var home = new McpInstallerTests.TestDirectory();
        var path = Path.Combine(home.Path, "config.toml");
        var original = "theme = 'keep'\n";
        var ct = TestContext.Current.CancellationToken;
        await File.WriteAllTextAsync(path, original, ct);
        var target = new McpInstallTarget("Codex", path, McpConfigFormat.Toml, "mcp_servers", true, "/test/codex", McpClientKind.Codex);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = new McpInstaller(new FixtureRunner(async token =>
        {
            entered.SetResult();
            await resume.Task.WaitAsync(token);
            await File.WriteAllTextAsync(path, McpConfiguration.Merge(original, target.Format, target.RootKey, Launch), token);
            return 0;
        }));
        var install = first.InstallAsync([target], Launch, false, ct);
        var secondLaunch = new McpLaunch("/test/another-tgcli", Launch.Arguments);
        var second = new McpInstaller(new FixtureRunner(async token =>
        {
            await File.WriteAllTextAsync(path, McpConfiguration.Merge(original, target.Format, target.RootKey, secondLaunch), token);
            return 0;
        }));
        try
        {
            await entered.Task.WaitAsync(ct);
            var result = Assert.Single(await second.InstallAsync([target with { Cli = useNative ? target.Cli : null }], secondLaunch, false, ct));
            Assert.Equal("failed", result.Status);
            Assert.Contains("another tgcli installer", result.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(original, await File.ReadAllTextAsync(path, ct));
            Assert.Single(Directory.GetFiles(home.Path, "*.tgcli-backup-*"));
        }
        finally
        {
            resume.TrySetResult();
            Assert.Equal("installed", Assert.Single(await install).Status);
        }
    }

    [Fact]
    public async Task NativeLaunchThatDropsUnrelatedSavedSettingsReportsFailureAndKeepsBackup()
    {
        using var home = new McpInstallerTests.TestDirectory();
        var path = Path.Combine(home.Path, "config.toml");
        var original = "theme = 'keep'\n[mcp_servers.other]\ncommand = 'leave-alone'\n";
        var ct = TestContext.Current.CancellationToken;
        await File.WriteAllTextAsync(path, original, ct);
        var target = new McpInstallTarget("Codex", path, McpConfigFormat.Toml, "mcp_servers", true, "/test/codex", McpClientKind.Codex);
        var installer = new McpInstaller(new FixtureRunner(async token =>
        {
            await File.WriteAllTextAsync(path, McpConfiguration.Merge(null, target.Format, target.RootKey, Launch), token);
            return 0;
        }));

        var result = Assert.Single(await installer.InstallAsync([target], Launch, false, ct));

        Assert.Equal("failed", result.Status);
        Assert.Contains("unrelated", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(original, await File.ReadAllTextAsync(Assert.Single(Directory.GetFiles(home.Path, "*.tgcli-backup-*")), ct));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InstallerPreservesOriginalEncodingAndExactBackup(bool eclipse)
    {
        using var home = new McpInstallerTests.TestDirectory();
        var path = Path.Combine(home.Path, "settings");
        var format = eclipse ? McpConfigFormat.EclipsePreferences : McpConfigFormat.Json;
        var original = format == McpConfigFormat.Json
            ? new UTF8Encoding(encoderShouldEmitUTF8Identifier: true).GetPreamble().Concat(Encoding.UTF8.GetBytes("""{"name":"café"}""")).ToArray()
            : Encoding.Latin1.GetBytes("# café\rother=café\r");
        var ct = TestContext.Current.CancellationToken;
        await File.WriteAllBytesAsync(path, original, ct);
        var target = new McpInstallTarget("Profile", path, format, "servers", true);

        var result = Assert.Single(await new McpInstaller(new NoRunner()).InstallAsync([target], Launch, false, ct));

        Assert.Equal("installed", result.Status);
        Assert.Equal(original, await File.ReadAllBytesAsync(Assert.Single(Directory.GetFiles(home.Path, "*.tgcli-backup-*")), ct));
        var saved = await File.ReadAllBytesAsync(path, ct);
        if (format == McpConfigFormat.Json) Assert.True(saved.AsSpan().StartsWith(original.AsSpan(0, 3)));
        else Assert.True(saved.AsSpan().StartsWith(original));
    }

    [Fact]
    public async Task InvalidUtf8JsonIsRejectedWithoutReplacingBytes()
    {
        using var home = new McpInstallerTests.TestDirectory();
        var path = Path.Combine(home.Path, "settings.json");
        var original = Encoding.Latin1.GetBytes("""{"name":"café"}""");
        var ct = TestContext.Current.CancellationToken;
        await File.WriteAllBytesAsync(path, original, ct);
        var target = new McpInstallTarget("Profile", path, McpConfigFormat.Json, "servers", true);

        var result = Assert.Single(await new McpInstaller(new NoRunner()).InstallAsync([target], Launch, false, ct));

        Assert.Equal("failed", result.Status);
        Assert.Equal(original, await File.ReadAllBytesAsync(path, ct));
        Assert.Single(Directory.GetFiles(home.Path));
    }

    [Fact]
    public async Task NativeRunnerHonorsCancellationBeforeAttemptingLaunch()
    {
        using var home = new McpInstallerTests.TestDirectory();
        using var stopped = new CancellationTokenSource();
        await stopped.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new McpProcessRunner().RunAsync(Path.Combine(home.Path, "nonexistent-launcher"), [], stopped.Token));
    }

    private sealed class FixtureRunner(Func<CancellationToken, Task<int>> run) : IMcpProcessRunner
    {
        public Task<int> RunAsync(string executable, IReadOnlyList<string> arguments, CancellationToken cancellationToken) =>
            run(cancellationToken);
    }

    private sealed class NoRunner : IMcpProcessRunner
    {
        public Task<int> RunAsync(string executable, IReadOnlyList<string> arguments, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("A file adapter must not launch a process.");
    }
}
