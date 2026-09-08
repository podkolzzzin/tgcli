using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace TgCli.Tests;

public sealed class McpProtocolTests
{
    [Fact]
    public async Task ModernDirectCallsWorkWithoutHandshakeAndEmitRequiredFields()
    {
        await using var server = new TestServer();
        var list = await server.Request("tools/list", new());
        var result = list["result"]!;
        Assert.Equal("complete", result["resultType"]!.GetValue<string>());
        Assert.Equal(300000, result["ttlMs"]!.GetValue<int>());
        Assert.Equal("private", result["cacheScope"]!.GetValue<string>());
        var names = result["tools"]!.AsArray().Select(t => t!["name"]!.GetValue<string>()).ToArray();
        Assert.Equal(12, names.Length);
        Assert.Equal(names.Order(StringComparer.Ordinal), names);
        var call = await server.Request("tools/call", new() { ["name"] = "message_get", ["arguments"] = new JsonObject { ["chat_id"] = "-1001234567890", ["message_id"] = "9223372036854775807" } });
        Assert.Equal("-1001234567890", call["result"]!["structuredContent"]!["chat_id"]!.GetValue<string>());
        Assert.Equal("complete", call["result"]!["resultType"]!.GetValue<string>());
        await server.Stop();
    }

    [Fact]
    public async Task DiscoverAdvertisesModernProtocolAndUnknownVersionDoesNotExecuteTool()
    {
        await using var server = new TestServer();
        var discovery = await server.Request("server/discover", new());
        Assert.Contains("2026-07-28", discovery.ToJsonString());
        Assert.Null(discovery["result"]!["capabilities"]!["logging"]);
        var rejected = await server.Request("tools/call", new() { ["name"] = "diagnostics" }, version: "2099-01-01");
        Assert.Equal(-32022, rejected["error"]!["code"]!.GetValue<int>());
        Assert.Equal("2026-07-28", rejected["error"]!["data"]!["supported"]![0]!.GetValue<string>());
        var next = await server.Request("tools/call", new() { ["name"] = "diagnostics" });
        Assert.Equal("diagnostics", next["result"]!["structuredContent"]!["tool"]!.GetValue<string>());
    }

    [Fact]
    public async Task ModernMetadataIsRequiredOnEveryRequest()
    {
        await using var server = new TestServer();
        await server.Request("tools/list", new());
        var missing = await server.Request("tools/call", new() { ["name"] = "diagnostics" }, modern: false);
        Assert.Equal(-32602, missing["error"]!["code"]!.GetValue<int>());
        Assert.NotNull((await server.Request("tools/list", new()))["result"]);
    }

    [Theory]
    [InlineData("ping")]
    [InlineData("logging/setLevel")]
    [InlineData("resources/subscribe")]
    public async Task RemovedModernMethodsReturnMethodNotFound(string method)
    {
        await using var server = new TestServer();
        var response = await server.Request(method, new() { ["level"] = "info", ["uri"] = "test://resource" });
        Assert.Equal(-32601, response["error"]!["code"]!.GetValue<int>());
    }

    [Fact]
    public async Task UnsupportedFirstRequestDoesNotPoisonLaterRequests()
    {
        await using var server = new TestServer();
        var rejected = await server.Request("tools/list", new(), version: "2099-01-01");
        Assert.Equal(-32022, rejected["error"]!["code"]!.GetValue<int>());
        Assert.NotNull((await server.Request("tools/list", new()))["result"]);
    }

    [Fact]
    public async Task UnnegotiatedRequestWithoutMetadataIsRejectedAndDoesNotPoisonConnection()
    {
        await using var server = new TestServer();
        var rejected = await server.Request("tools/list", new(), modern: false);
        Assert.Equal(-32602, rejected["error"]!["code"]!.GetValue<int>());
        Assert.NotNull((await server.Request("tools/list", new()))["result"]);
    }

    [Theory]
    [InlineData("2025-11-25")]
    [InlineData("2025-06-18")]
    [InlineData("2024-11-05")]
    public async Task LegacyClientsStillInitialize(string version)
    {
        await using var server = new TestServer();
        var init = await server.Request("initialize", new() { ["protocolVersion"] = version, ["capabilities"] = new JsonObject(), ["clientInfo"] = new JsonObject { ["name"] = "legacy-test", ["version"] = "1" } }, modern: false);
        Assert.Equal(version, init["result"]!["protocolVersion"]!.GetValue<string>());
        await server.Notify("notifications/initialized", new());
        var list = await server.Request("tools/list", new(), modern: false);
        Assert.Equal(12, list["result"]!["tools"]!.AsArray().Count);
        Assert.Null(list["result"]!["ttlMs"]);
        var call = await server.Request("tools/call", new() { ["name"] = "diagnostics" }, modern: false);
        Assert.NotNull(call["result"]!["content"]);
    }

    [Fact]
    public async Task UnsupportedVersionAfterLegacyInitializeDoesNotChangeConnectionMode()
    {
        await using var server = new TestServer();
        var init = await server.Request("initialize", new()
        {
            ["protocolVersion"] = "2025-11-25",
            ["capabilities"] = new JsonObject(),
            ["clientInfo"] = new JsonObject { ["name"] = "legacy-test", ["version"] = "1" }
        }, modern: false);
        Assert.Equal("2025-11-25", init["result"]!["protocolVersion"]!.GetValue<string>());
        await server.Notify("notifications/initialized", new());

        var rejected = await server.Request("tools/list", new(), version: "2099-01-01");
        Assert.Equal(-32022, rejected["error"]!["code"]!.GetValue<int>());
        Assert.NotNull((await server.Request("tools/list", new(), modern: false))["result"]);
    }

    [Fact]
    public async Task ErrorsRemainLocalToRequestAndUnknownToolsAreProtocolErrors()
    {
        await using var server = new TestServer();
        var invalid = await server.Request("tools/call", new() { ["name"] = "message_get", ["arguments"] = new JsonObject { ["chat_id"] = 123 } });
        Assert.True(invalid["result"]!["isError"]!.GetValue<bool>());
        var missing = await server.Request("tools/call", new() { ["name"] = "does_not_exist" });
        Assert.Equal(-32602, missing["error"]!["code"]!.GetValue<int>());
        var failure = await server.Request("tools/call", new() { ["name"] = "chat_search", ["arguments"] = new JsonObject { ["query"] = "error" } });
        Assert.True(failure["result"]!["isError"]!.GetValue<bool>());
        Assert.NotNull((await server.Request("tools/list", new()))["result"]);
    }

    [Fact]
    public async Task CancellationDoesNotCloseConnectionOrEmitLateResponse()
    {
        await using var server = new TestServer();
        var slowId = await server.Send("tools/call", new() { ["name"] = "chat_search", ["arguments"] = new JsonObject { ["query"] = "delay" } });
        // A following successful request proves the shared transport is processing messages.
        await server.Request("tools/list", new());
        await server.Notify("notifications/cancelled", new() { ["requestId"] = slowId });
        await server.Request("tools/call", new() { ["name"] = "diagnostics" });
        await server.Stop();
        Assert.DoesNotContain(server.Received, r => r["id"]?.GetValue<int>() == slowId);
    }

    internal sealed class TestServer : IAsyncDisposable
    {
        private readonly Process _process;
        private readonly Task<string> _stderr;
        private int _id;
        internal List<JsonNode> Received { get; } = [];
        internal TestServer()
        {
            var repo = new DirectoryInfo(AppContext.BaseDirectory);
            while (repo is not null && !File.Exists(Path.Combine(repo.FullName, "TgCli.csproj"))) repo = repo.Parent;
            if (repo is null) throw new IOException("Could not find test worker.");
#if DEBUG
            var config = "Debug";
#else
            var config = "Release";
#endif
            var worker = Path.Combine(repo.FullName, "TgCli.Tests", "McpTestServer", "bin", config, "net10.0", "TgCli.McpTestServer.dll");
            var start = new ProcessStartInfo("dotnet") { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            start.ArgumentList.Add(worker);
            _process = Process.Start(start)!;
            _stderr = _process.StandardError.ReadToEndAsync();
        }
        internal async Task<int> Send(string method, JsonObject parameters, bool modern = true, string version = "2026-07-28")
        {
            var id = ++_id;
            if (modern) parameters["_meta"] = new JsonObject { ["io.modelcontextprotocol/protocolVersion"] = version, ["io.modelcontextprotocol/clientCapabilities"] = new JsonObject(), ["io.modelcontextprotocol/clientInfo"] = new JsonObject { ["name"] = "protocol-test", ["version"] = "1" } };
            await Write(new() { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = parameters });
            return id;
        }
        internal async Task<JsonNode> Request(string method, JsonObject parameters, bool modern = true, string version = "2026-07-28")
        {
            var id = await Send(method, parameters, modern, version);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            while (true)
            {
                var line = await _process.StandardOutput.ReadLineAsync(timeout.Token) ?? throw new IOException("Server exited: " + await _stderr);
                var message = JsonNode.Parse(line)!;
                Assert.False(message["method"] is not null && message["id"] is not null, "Server initiated a forbidden request.");
                Received.Add(message);
                if (message["id"]?.GetValue<int>() == id) return message;
            }
        }
        internal Task Notify(string method, JsonObject parameters) => Write(new() { ["jsonrpc"] = "2.0", ["method"] = method, ["params"] = parameters });
        private async Task Write(JsonObject message) { await _process.StandardInput.WriteLineAsync(message.ToJsonString()); await _process.StandardInput.FlushAsync(); }
        internal async Task Stop()
        {
            _process.StandardInput.Close();
            await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            var remainder = await _process.StandardOutput.ReadToEndAsync();
            foreach (var line in remainder.Split('\n', StringSplitOptions.RemoveEmptyEntries)) Received.Add(JsonNode.Parse(line)!);
            Assert.Equal(0, _process.ExitCode);
            Assert.Equal("", await _stderr);
        }
        public async ValueTask DisposeAsync()
        {
            if (!_process.HasExited) { _process.Kill(true); await _process.WaitForExitAsync(); }
            _process.Dispose();
        }
    }
}
