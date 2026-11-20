using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Xunit;

namespace TgCli.Tests;

public sealed class McpReleaseRegressionTests
{
    [Theory]
    [InlineData("""{"io.modelcontextprotocol/clientInfo":"fixture","io.modelcontextprotocol/clientCapabilities":{}}""")]
    [InlineData("""{"io.modelcontextprotocol/clientCapabilities":{"sampling":true}}""")]
    [InlineData("""{"io.modelcontextprotocol/clientCapabilities":{},"io.modelcontextprotocol/logLevel":"fixture"}""")]
    public async Task RejectedModernMetadataLeavesLegacyInitializeAvailable(string rawMetadata)
    {
        await using var server = new McpProtocolTests.TestServer();
        var meta = JsonNode.Parse(rawMetadata)!.AsObject();
        meta["io.modelcontextprotocol/protocolVersion"] = "2026-07-28";

        var rejected = await server.Request("server/discover", new() { ["_meta"] = meta }, modern: false);

        Assert.NotNull(rejected["error"]);
        var init = await server.Request("initialize", LegacyInitialize(), modern: false);
        Assert.Equal("2025-11-25", init["result"]?["protocolVersion"]?.GetValue<string>());
        await server.Notify("notifications/initialized", new());
        Assert.NotNull((await server.Request("tools/list", new(), modern: false))["result"]);
    }

    [Fact]
    public async Task SuccessfulModernDiscoveryRejectsAPipelinedLegacyInitialize()
    {
        await using var server = new McpProtocolTests.TestServer();

        var discover = await server.Send("server/discover", new());
        var initialize = await server.Request("initialize", LegacyInitialize(), modern: false);

        Assert.Contains(server.Received, response => response["id"]?.GetValue<int>() == discover &&
            response["result"]?["supportedVersions"]?[0]?.GetValue<string>() == "2026-07-28");
        Assert.Equal(-32600, initialize["error"]?["code"]?.GetValue<int>());
        Assert.NotNull((await server.Request("tools/list", new()))["result"]);
    }

    [Fact]
    public async Task DuplicateMetadataPropertyReturnsBoundedErrorAndPreservesConnection()
    {
        await using var server = new McpProtocolTests.TestServer();
        var metadata = JsonNode.Parse("""
            {
              "io.modelcontextprotocol/protocolVersion": "2026-07-28",
              "io.modelcontextprotocol/protocolVersion": "synthetic-duplicate-value",
              "io.modelcontextprotocol/clientCapabilities": {}
            }
            """);

        var rejected = await server.Request("tools/list", new() { ["_meta"] = metadata }, modern: false);

        Assert.Equal(-32602, rejected["error"]?["code"]?.GetValue<int>());
        Assert.True(Encoding.UTF8.GetByteCount(rejected.ToJsonString()) < 1024);
        Assert.DoesNotContain("synthetic-duplicate-value", rejected.ToJsonString());
        Assert.NotNull((await server.Request("tools/list", new()))["result"]);
        await server.Stop();
    }

    [Fact]
    public void CodexTomlLaunchPreservesUnicodeScalarsAndEscapedArgumentCharacters()
    {
        const string original = "# Preserve this comment\nmodel = 'fixture'\n[mcp_servers]\n[mcp_servers.other]\ncommand = 'existing'\n";
        var launch = new McpLaunch("/fixture/launch 🦜", ["C:\\path 🦜", "quotes \" and \\", "first\nsecond\tline\rreturn", "\u0001\u007f"]);

        var merged = McpConfiguration.Merge(original, McpConfigFormat.Toml, "mcp_servers", launch);

        Assert.StartsWith(original, merged);
        Assert.True(launch.Matches(McpConfiguration.ReadEntry(merged, McpConfigFormat.Toml, "mcp_servers")));
        Assert.Equal(merged, McpConfiguration.Merge(merged, McpConfigFormat.Toml, "mcp_servers", launch));
    }

    [Fact]
    public async Task CodexInstallDryRunAcceptsExistingSupplementaryUnicodeSessionPath()
    {
        using var home = new McpInstallerTests.TestDirectory();
        var ct = TestContext.Current.CancellationToken;
        var codexHome = Directory.CreateDirectory(Path.Combine(home.Path, "codex")).FullName;
        var profile = Path.Combine(codexHome, "config.toml");
        const string original = "model = 'fixture'\n";
        await File.WriteAllTextAsync(profile, original, ct);
        var session = Directory.CreateDirectory(Path.Combine(home.Path, "session 🦜")).FullName;
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        start.Environment["HOME"] = home.Path;
        start.Environment["USERPROFILE"] = home.Path;
        start.Environment["CODEX_HOME"] = codexHome;
        start.Environment.Remove("PATH");
        foreach (var argument in new[] { typeof(McpConfiguration).Assembly.Location, "mcp", "install", "codex", "--dry-run", "--session", session })
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        var output = await stdout;
        var errors = await stderr;

        Assert.True(process.ExitCode == 0, output + errors);
        Assert.Contains("would-install", output);
        Assert.Equal(original, await File.ReadAllTextAsync(profile, ct));
        Assert.Equal(profile, Assert.Single(Directory.GetFiles(codexHome)));
    }

    [Fact]
    public void InaccessibleEclipseDirectoryDoesNotHideReadableSiblingRegistry()
    {
        if (OperatingSystem.IsWindows()) return;
        using var home = new McpInstallerTests.TestDirectory();
        var eclipse = Directory.CreateDirectory(Path.Combine(home.Path, ".eclipse"));
        var denied = eclipse.CreateSubdirectory("aaa-permission-denied");
        var registry = eclipse.CreateSubdirectory("zzz-readable-registry");
        var workspace = Directory.CreateDirectory(Path.Combine(home.Path, "synthetic-workspace"));
        Directory.CreateDirectory(Path.Combine(workspace.FullName, ".metadata", ".plugins", "com.microsoft.copilot.eclipse.ui"));
        File.WriteAllText(Path.Combine(registry.FullName, "org.eclipse.ui.ide.prefs"), $"RECENT_WORKSPACES={workspace.FullName}\n");
        var original = File.GetUnixFileMode(denied.FullName);
        File.SetUnixFileMode(denied.FullName, UnixFileMode.None);
        try
        {
            try
            {
                // Elevated test runners can bypass the mode bits.
                _ = Directory.GetFiles(denied.FullName);
                return;
            }
            catch (UnauthorizedAccessException) { }

            var environment = new McpInstallEnvironment(home.Path, "linux", new Dictionary<string, string>());
            var targets = McpClientDiscovery.Find("copilot", environment);

            Assert.Contains(targets, target => target.DiscoveryError is not null && target.Path == denied.FullName);
            Assert.Contains(targets, target => target.Installed && target.DiscoveryError is null &&
                target.Client == "Eclipse Copilot / " + workspace.FullName);
        }
        finally { File.SetUnixFileMode(denied.FullName, original); }
    }

    private static JsonObject LegacyInitialize() => new()
    {
        ["protocolVersion"] = "2025-11-25",
        ["capabilities"] = new JsonObject(),
        ["clientInfo"] = new JsonObject { ["name"] = "legacy-fixture", ["version"] = "1" }
    };
}
