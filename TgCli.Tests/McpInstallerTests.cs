using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace TgCli.Tests;

public sealed class McpInstallerTests
{
    private static McpLaunch Launch => new("/opt/Мої програми/tgcli", ["mcp", "--session", "/tmp/private session"]);

    [Theory]
    [InlineData("{}")]
    [InlineData("{\n// retain comment\n\"theme\":\"dark\",\n}")]
    [InlineData("{/* comment */\"mcpServers\": {/* servers */},\"other\":42}")]
    [InlineData("{\"mcpServers\":{\"another\":{\"command\":\"keep\"},},\"secret\":\"preserve\"}")]
    public void JsonMergePreservesOtherContentAndIsIdempotent(string original)
    {
        var merged = McpConfiguration.Merge(original, McpConfigFormat.Json, "mcpServers", Launch);
        Assert.True(Launch.Matches(McpConfiguration.ReadEntry(merged, McpConfigFormat.Json, "mcpServers")));
        Assert.Equal(merged, McpConfiguration.Merge(merged, McpConfigFormat.Json, "mcpServers", Launch));
        var before = McpConfiguration.ReadJson(original);
        var after = McpConfiguration.ReadJson(merged);
        after["mcpServers"]!.AsObject().Remove("tgcli");
        if (!before.ContainsKey("mcpServers")) after.Remove("mcpServers");
        Assert.True(JsonNode.DeepEquals(before, after));
        if (original.Contains("retain comment")) Assert.Contains("// retain comment", merged);
        if (original.Contains("/* servers */")) Assert.Contains("/* servers */", merged);
    }

    [Theory]
    [InlineData("{\"mcpServers\":[],\"other\":1}")]
    [InlineData("{\"mcpServers\":{},\"mcpServers\":{}}")]
    [InlineData("{\"mcpServers\":{\"tgcli\":{\"command\":\"different\"}}}")]
    [InlineData("not json")]
    [InlineData("")]
    public void InvalidOrConflictingConfigurationIsNeverReplaced(string text)
    {
        Assert.ThrowsAny<Exception>(() => McpConfiguration.Merge(text, McpConfigFormat.Json, "mcpServers", Launch));
    }

    [Fact]
    public void TomlAppendPreservesCommentsAndNestedUnrelatedSettings()
    {
        var original = "# user's comment\nmodel = 'configured-model'\n[mcp_servers.existing]\ncommand = 'leave-alone'\n[mcp_servers.existing.env]\nSECRET = 'do-not-log'\n";
        var merged = McpConfiguration.Merge(original, McpConfigFormat.Toml, "mcp_servers", Launch);
        Assert.StartsWith(original, merged);
        Assert.True(Launch.Matches(McpConfiguration.ReadEntry(merged, McpConfigFormat.Toml, "mcp_servers")));
        Assert.Equal(merged, McpConfiguration.Merge(merged, McpConfigFormat.Toml, "mcp_servers", Launch));
    }

    [Fact]
    public void EclipsePreferencesRoundTripUnicodeAndWindowsEscapes()
    {
        var launch = new McpLaunch("C:\\Program Files\\tgcli\\tgcli.exe", ["mcp", "--session", "C:\\Users\\Андрій\\session"]);
        var original = "eclipse.preferences.version=1\n# existing preferences\nother=leave\\:this\nmcp={\"servers\"\\:{\"other\"\\:{\"command\"\\:\"keep\"}}}\n";
        var merged = McpConfiguration.Merge(original, McpConfigFormat.EclipsePreferences, "servers", launch);
        Assert.Contains("other=leave\\:this", merged);
        Assert.Contains("# existing preferences", merged);
        Assert.True(launch.Matches(McpConfiguration.ReadEntry(merged, McpConfigFormat.EclipsePreferences, "servers")));
        Assert.Equal(merged, McpConfiguration.Merge(merged, McpConfigFormat.EclipsePreferences, "servers", launch));
    }

    [Fact]
    public async Task AtomicWritesKeepPrivateBackupAndDetectConcurrentChanges()
    {
        using var temp = new TestDirectory();
        var path = Path.Combine(temp.Path, "settings.json");
        await File.WriteAllTextAsync(path, "{}", TestContext.Current.CancellationToken);
        await McpConfiguration.WriteAtomicAsync(path, "{}", "{\"updated\":true}", TestContext.Current.CancellationToken);
        Assert.Equal("{}", await File.ReadAllTextAsync(Directory.GetFiles(temp.Path, "*.tgcli-backup-*").Single(), TestContext.Current.CancellationToken));
        if (!OperatingSystem.IsWindows()) Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
        await Assert.ThrowsAsync<IOException>(() => McpConfiguration.WriteAtomicAsync(path, "{}", "lost-update", TestContext.Current.CancellationToken));
        Assert.Equal("{\"updated\":true}", await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
        Assert.Empty(Directory.GetFiles(temp.Path, "*.tmp"));
    }

    [Fact]
    public async Task InstallsIndependentTargetsAfterFailureAndDeduplicatesSharedConfig()
    {
        using var temp = new TestDirectory();
        var bad = Path.Combine(temp.Path, "bad.json"); await File.WriteAllTextAsync(bad, "broken", TestContext.Current.CancellationToken);
        var good = Path.Combine(temp.Path, "good.json");
        var targets = new[]
        {
            new McpInstallTarget("bad", bad, McpConfigFormat.Json, "servers", true),
            new McpInstallTarget("good", good, McpConfigFormat.Json, "servers", true),
            new McpInstallTarget("shared", good, McpConfigFormat.Json, "servers", true),
            new McpInstallTarget("absent", Path.Combine(temp.Path, "absent.json"), McpConfigFormat.Json, "servers", false)
        };
        var results = await new McpInstaller(new RejectRunner()).InstallAsync(targets, Launch, false, TestContext.Current.CancellationToken);
        Assert.Equal(new[] { "failed", "installed", "installed", "not-installed" }, results.Select(x => x.Status));
        Assert.Contains("Shares configuration", results[2].Message);
        Assert.False(File.Exists(targets[3].Path));
        Assert.Equal("broken", await File.ReadAllTextAsync(bad, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DryRunDoesNotCreateProfilesOrLaunchClient()
    {
        using var temp = new TestDirectory();
        var target = new McpInstallTarget("Claude Code CLI", Path.Combine(temp.Path, "new", "settings.json"), McpConfigFormat.Json, "mcpServers", true, "/fake/claude");
        var results = await new McpInstaller(new RejectRunner()).InstallAsync([target], Launch, true, TestContext.Current.CancellationToken);
        Assert.Equal("would-install", results.Single().Status);
        Assert.False(Directory.Exists(Path.GetDirectoryName(target.Path)));
    }

    [Fact]
    public async Task ClientSuccessWithoutPersistedEntryIsFailure()
    {
        using var temp = new TestDirectory();
        var target = new McpInstallTarget("Codex CLI + Desktop", Path.Combine(temp.Path, "config.toml"), McpConfigFormat.Toml, "mcp_servers", true, "/fake/codex");
        var runner = new DelegateRunner((exe, args, token) => Task.FromResult(new McpProcessResult(0, "success", "")));
        var results = await new McpInstaller(runner).InstallAsync([target], Launch, false, TestContext.Current.CancellationToken);
        Assert.Equal("failed", results.Single().Status);
        Assert.Contains("verification", results.Single().Message);
    }

    [Fact]
    public async Task NativeCommandReceivesSeparateArgumentsAndIsNotRepeated()
    {
        using var temp = new TestDirectory();
        var target = new McpInstallTarget("Claude Code CLI", Path.Combine(temp.Path, "claude.json"), McpConfigFormat.Json, "mcpServers", true, "/fake/claude");
        var calls = 0;
        var runner = new DelegateRunner(async (exe, args, token) =>
        {
            calls++;
            Assert.Equal(new[] { "mcp", "add", "--transport", "stdio", "--scope", "user", "tgcli", "--", Launch.Command }.Concat(Launch.Arguments), args);
            await File.WriteAllTextAsync(target.Path, McpConfiguration.Merge(null, target.Format, target.RootKey, Launch), token);
            return new(0, "", "");
        });
        var installer = new McpInstaller(runner);
        Assert.Equal("installed", (await installer.InstallAsync([target], Launch, false, TestContext.Current.CancellationToken)).Single().Status);
        Assert.Equal("already-installed", (await installer.InstallAsync([target], Launch, false, TestContext.Current.CancellationToken)).Single().Status);
        Assert.Equal(1, calls);
    }

    [Fact]
    public void DiscoveryFindsDesktopWithoutCliAndHonorsConfigHome()
    {
        using var temp = new TestDirectory();
        Directory.CreateDirectory(Path.Combine(temp.Path, "Library", "Application Support", "Claude"));
        var codexHome = Path.Combine(temp.Path, "custom codex"); Directory.CreateDirectory(codexHome);
        var env = new McpInstallEnvironment(temp.Path, "macos", new Dictionary<string, string> { ["CODEX_HOME"] = codexHome });
        var claude = McpClientDiscovery.Find("claude", env);
        Assert.False(claude[0].Installed); Assert.True(claude[1].Installed); Assert.Null(claude[1].Cli);
        var codex = McpClientDiscovery.Find("codex", env).Single();
        Assert.True(codex.Installed); Assert.Equal(Path.Combine(codexHome, "config.toml"), codex.Path); Assert.Null(codex.Cli);
    }

    [Fact]
    public void DiscoveryIncludesAllCopilotAdaptersAndExistingProfiles()
    {
        using var temp = new TestDirectory();
        foreach (var dir in new[] { ".copilot", ".config/github-copilot/intellij", ".config/Code/User", ".config/Code/User/profiles/work", ".vscode/extensions/github.copilot-chat-1.0" })
            Directory.CreateDirectory(Path.Combine(temp.Path, dir));
        var workspace = Path.Combine(temp.Path, "known-eclipse");
        var prefs = Path.Combine(workspace, ".metadata", ".plugins", "org.eclipse.core.runtime", ".settings");
        Directory.CreateDirectory(prefs); File.WriteAllText(Path.Combine(prefs, "com.microsoft.copilot.eclipse.ui.prefs"), "eclipse.preferences.version=1\n");
        var targets = McpClientDiscovery.Find("copilot", new(temp.Path, "linux", new Dictionary<string, string>()), workspace);
        Assert.Equal(5, targets.Count(x => x.Installed));
        Assert.Contains(targets, t => t.Client.Contains("JetBrains"));
        Assert.Contains(targets, t => t.Client.Contains("Xcode"));
        Assert.Contains(targets, t => t.Client.Contains("Visual Studio"));
        Assert.Contains(targets, t => t.Client.Contains("Eclipse") && t.Installed);
    }

    [Theory]
    [InlineData("windows", "AppData/Roaming/Claude")]
    [InlineData("macos", "Library/Application Support/Claude")]
    [InlineData("linux", ".config/Claude")]
    public void ClaudeDesktopConfigPathsDoNotDependOnCli(string os, string profile)
    {
        using var temp = new TestDirectory();
        var directory = Path.Combine(temp.Path, profile.Replace('/', Path.DirectorySeparatorChar)); Directory.CreateDirectory(directory);
        var targets = McpClientDiscovery.Find("claude", new(temp.Path, os, new Dictionary<string, string>()));
        var desktop = targets.Single(t => t.Client == "Claude Desktop");
        Assert.True(desktop.Installed);
        Assert.Equal(Path.Combine(directory, "claude_desktop_config.json"), desktop.Path);
        Assert.Null(desktop.Cli);
    }

    [Fact]
    public async Task BadProfileRegistryDoesNotBlockOtherCopilotTargets()
    {
        using var temp = new TestDirectory();
        Directory.CreateDirectory(Path.Combine(temp.Path, ".copilot"));
        var profile = Path.Combine(temp.Path, ".config", "Code", "User", "profiles", "bad");
        Directory.CreateDirectory(profile);
        File.WriteAllText(Path.Combine(profile, "extensions.json"), "invalid");
        var targets = McpClientDiscovery.Find("copilot", new(temp.Path, "linux", new Dictionary<string, string>()));
        var results = await new McpInstaller(new RejectRunner()).InstallAsync(targets, Launch, false, TestContext.Current.CancellationToken);
        Assert.Contains(results, r => r.Client.Contains("CLI") && r.Status == "installed");
        Assert.Contains(results, r => r.Client.EndsWith("bad") && r.Status == "failed");
        Assert.False(File.Exists(Path.Combine(profile, "mcp.json")));
    }

    [Fact]
    public void PortableVsCodeUsesItsOwnExtensionsAndProfileStorage()
    {
        using var temp = new TestDirectory();
        var portable = Path.Combine(temp.Path, "portable");
        Directory.CreateDirectory(Path.Combine(portable, "user-data", "User"));
        Directory.CreateDirectory(Path.Combine(portable, "extensions", "github.copilot-chat-1.0"));
        var targets = McpClientDiscovery.Find("copilot", new(temp.Path, "linux", new Dictionary<string, string> { ["VSCODE_PORTABLE"] = portable }));
        Assert.Contains(targets, t => t.Installed && t.Path == Path.Combine(portable, "user-data", "User", "mcp.json"));
    }

    [Fact]
    public void WindowsEnvironmentUsesCaseInsensitivePathAndSemicolonSeparators()
    {
        using var temp = new TestDirectory();
        var bin = Path.Combine(temp.Path, "Program Files", "client"); Directory.CreateDirectory(bin);
        var executable = Path.Combine(bin, "codex.exe"); File.WriteAllText(executable, "test launcher");
        var env = new McpInstallEnvironment(temp.Path, "windows", new Dictionary<string, string>
        { ["Path"] = Path.Combine(temp.Path, "absent") + ";" + bin, ["AppData"] = "configured-appdata" });
        Assert.Equal(executable, env.Executable("codex"));
        Assert.Equal("configured-appdata", env.AppData);
    }

    private sealed class RejectRunner : IMcpProcessRunner
    { public Task<McpProcessResult> RunAsync(string exe, IReadOnlyList<string> args, CancellationToken ct) => throw new InvalidOperationException("Runner must not be called."); }
    private sealed class DelegateRunner(Func<string, IReadOnlyList<string>, CancellationToken, Task<McpProcessResult>> run) : IMcpProcessRunner
    { public Task<McpProcessResult> RunAsync(string exe, IReadOnlyList<string> args, CancellationToken ct) => run(exe, args, ct); }
    internal sealed class TestDirectory : IDisposable
    {
        internal string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "tgcli-mcp-tests-" + Guid.NewGuid().ToString("N"));
        internal TestDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
