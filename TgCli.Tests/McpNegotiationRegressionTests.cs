using System;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace TgCli.Tests;

public sealed class McpNegotiationRegressionTests
{
    [Fact]
    public async Task FailedInitializeLeavesTransportOpenForAValidLegacyRetry()
    {
        await using var server = new McpProtocolTests.TestServer();

        Assert.NotNull((await server.Request("initialize", new(), modern: false))["error"]);
        Assert.Equal(-32602, (await server.Request("tools/list", new(), modern: false))["error"]?["code"]?.GetValue<int>());
        var retry = await server.Request("initialize", Initialize(), modern: false);
        Assert.Equal("2025-11-25", retry["result"]?["protocolVersion"]?.GetValue<string>());
        await server.Notify("notifications/initialized", new());
        Assert.NotNull((await server.Request("tools/list", new(), modern: false))["result"]);
    }

    [Fact]
    public async Task SdkRejectedInitializeAndPipelinedRequestDoNotNegotiateLegacyMode()
    {
        await using var server = new McpProtocolTests.TestServer();
        var parameters = Initialize();
        parameters["protocolVersion"] = "2026-07-28"; // This revision has no initialize handshake.

        var invalid = await server.Send("initialize", parameters, modern: false);
        var request = await server.Request("tools/list", new(), modern: false);
        Assert.Contains(server.Received, r => r["id"]?.GetValue<int>() == invalid && r["error"] is not null);
        Assert.Equal(-32602, request["error"]?["code"]?.GetValue<int>());
        Assert.NotNull((await server.Request("tools/list", new()))["result"]);
    }

    [Fact]
    public async Task ModernConnectionRejectsLegacyInitializeAndStaysModern()
    {
        await using var server = new McpProtocolTests.TestServer();

        Assert.NotNull((await server.Request("tools/list", new()))["result"]);
        Assert.NotNull((await server.Request("initialize", Initialize(), modern: false))["error"]);
        Assert.Equal(-32602, (await server.Request("tools/list", new(), modern: false))["error"]?["code"]?.GetValue<int>());
        Assert.NotNull((await server.Request("tools/list", new()))["result"]);
    }

    [Fact]
    public async Task LegacyConnectionRejectsModernRequestsAndStaysLegacy()
    {
        await using var server = new McpProtocolTests.TestServer();

        Assert.NotNull((await server.Request("initialize", Initialize(), modern: false))["result"]);
        await server.Notify("notifications/initialized", new());
        Assert.NotNull((await server.Request("tools/list", new()))["error"]);
        var list = await server.Request("tools/list", new(), modern: false);
        Assert.NotNull(list["result"]);
        Assert.Null(list["result"]!["ttlMs"]);
    }

    [Theory]
    [InlineData("argument")]
    [InlineData("tool")]
    [InlineData("version")]
    [InlineData("method")]
    public async Task HugeUnknownInputGetsABoundedErrorWithoutEcho(string location)
    {
        await using var server = new McpProtocolTests.TestServer();
        var huge = "synthetic-private-marker-" + new string('x', McpTools.MaxResponseBytes);
        var parameters = new JsonObject { ["name"] = location == "tool" ? huge : "diagnostics" };
        if (location == "argument") parameters["arguments"] = new JsonObject { [huge] = true };

        var response = await server.Request(location == "method" ? huge : "tools/call", parameters,
            version: location == "version" ? huge : "2026-07-28");

        var json = response.ToJsonString();
        Assert.True(Encoding.UTF8.GetByteCount(json) < McpTools.MaxResponseBytes);
        Assert.DoesNotContain("synthetic-private-marker-", json);
        if (location == "argument")
        {
            Assert.True(response["result"]!["isError"]!.GetValue<bool>());
            Assert.Contains("invalid_arguments", response["result"]!["content"]![0]!["text"]!.GetValue<string>());
        }
        else Assert.Equal(location == "method" ? -32601 : -32602, response["error"]?["code"]?.GetValue<int>());
        Assert.NotNull((await server.Request("tools/list", new()))["result"]);
    }

    [Fact]
    public async Task KnownMissingArgumentStillHasActionableFeedback()
    {
        await using var server = new McpProtocolTests.TestServer();

        var response = await server.Request("tools/call", new()
        {
            ["name"] = "chat_resolve",
            ["arguments"] = new JsonObject { ["chat"] = "synthetic-private-marker" }
        });
        var text = response["result"]!["content"]![0]!["text"]!.GetValue<string>();
        Assert.Contains("invalid_arguments", text);
        Assert.Contains("username", text);
        Assert.DoesNotContain("synthetic-private-marker", text);
    }

    [Fact]
    public async Task BackendExceptionDoesNotExposeItsInputOrOversizedMessage()
    {
        var tools = new McpTools(new FailingBackend());
        var result = await tools.CallAsync("diagnostics", null, TestContext.Current.CancellationToken);

        Assert.True(result.IsError);
        var text = Assert.IsType<ModelContextProtocol.Protocol.TextContentBlock>(Assert.Single(result.Content)).Text;
        Assert.Contains("operation_failed", text);
        Assert.DoesNotContain("synthetic-private-marker", text);
        Assert.True(Encoding.UTF8.GetByteCount(text) < McpTools.MaxResponseBytes);
    }

    private static JsonObject Initialize() => new()
    {
        ["protocolVersion"] = "2025-11-25",
        ["capabilities"] = new JsonObject(),
        ["clientInfo"] = new JsonObject { ["name"] = "legacy-fixture", ["version"] = "1" }
    };

    private sealed class FailingBackend : IMcpTelegramBackend
    {
        public Task<JsonObject> ExecuteAsync(string name, McpArguments arguments, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("synthetic-private-marker" + new string('é', McpTools.MaxResponseBytes));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
