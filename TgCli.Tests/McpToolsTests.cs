using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using TdLib;
using Xunit;

namespace TgCli.Tests;

public sealed class McpToolsTests
{
    private static McpArguments Args(string json) => new(JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json));

    [Theory]
    [InlineData("9223372036854775807")]
    [InlineData("-9223372036854775808")]
    [InlineData("-1001234567890")]
    public void DecimalIdsPreserveAll64Bits(string value)
    {
        var args = Args(new JsonObject { ["chat_id"] = value, ["message_id"] = "1" }.ToJsonString());
        args.Validate(McpTools.Catalog.Single(x => x.Name == "message_get").InputSchema);
        Assert.Equal(value, args.Id("chat_id").ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    [Theory]
    [InlineData("{\"chat_id\":123,\"message_id\":\"1\"}")]
    [InlineData("{\"chat_id\":\"9223372036854775808\",\"message_id\":\"1\"}")]
    [InlineData("{\"chat_id\":\"+123\",\"message_id\":\"1\"}")]
    [InlineData("{\"chat_id\":\"0\",\"message_id\":\"1\"}")]
    [InlineData("{\"chat_id\":\"1\",\"message_id\":\"1\",\"request_timeout\":301}")]
    [InlineData("{\"chat_id\":\"1\",\"message_id\":\"1\",\"request_timeout\":\"60\"}")]
    [InlineData("{\"chat_id\":\"1\",\"message_id\":\"1\",\"unknown\":true}")]
    public void InvalidArgumentsAreRejectedBeforeBackend(string json) =>
        Assert.Throws<ArgumentException>(() => Args(json).Validate(McpTools.Catalog.Single(x => x.Name == "message_get").InputSchema));

    [Fact]
    public void CursorKeepsAllForumOffsetsAndRejectsChangedFilters()
    {
        var args = Args("{\"chat_id\":\"-1001\",\"query\":\"hello\",\"limit\":30}");
        var fingerprint = args.Fingerprint("forum_topics");
        var cursor = new McpCursor(fingerprint, long.MaxValue, 1710000000, 21);
        Assert.Equal(cursor, McpCursor.Decode(cursor.Encode(), fingerprint));
        var changedPageSize = Args("{\"limit\":100,\"query\":\"hello\",\"chat_id\":\"-1001\",\"request_timeout\":10}");
        Assert.Equal(fingerprint, changedPageSize.Fingerprint("forum_topics"));
        Assert.Throws<ArgumentException>(() => McpCursor.Decode(cursor.Encode(), args.Fingerprint("chat_messages")));
        Assert.Throws<ArgumentException>(() => McpCursor.Decode(cursor.Encode(), Args("{\"chat_id\":\"-1002\"}").Fingerprint("forum_topics")));
        Assert.Throws<ArgumentException>(() => McpCursor.Decode("not a cursor", fingerprint));
        Assert.Throws<ArgumentException>(() => McpCursor.Decode(new string('A', 2049), fingerprint));
    }

    [Fact]
    public void McpConversionPreservesCliDataAndUsesStringsOnlyFor64BitIds()
    {
        var original = new JObject { ["chat_id"] = long.MinValue, ["topic_id"] = 21, ["file_id"] = 123,
            ["nested"] = new JObject { ["MessageId"] = long.MaxValue }, ["migrations"] = new JArray(-1001234567890L) };
        var result = McpTelegramBackend.ConvertObject(original);
        Assert.Equal(JTokenType.Integer, original["chat_id"]!.Type);
        Assert.Equal("-9223372036854775808", result["chat_id"]!.GetValue<string>());
        Assert.Equal("9223372036854775807", result["nested"]!["MessageId"]!.GetValue<string>());
        Assert.Equal(21, result["topic_id"]!.GetValue<int>());
        Assert.Equal(123, result["file_id"]!.GetValue<int>());
        Assert.Equal("-1001234567890", result["migrations"]![0]!.GetValue<string>());
    }

    [Fact]
    public async Task OversizedResultIsAnExplicitToolErrorAndServerRemainsUsable()
    {
        await using var backend = new Backend();
        var tools = new McpTools(backend);
        // Each copy fits alone; the complete result with both copies exceeds the cap.
        backend.Result = new() { ["text"] = new string('x', McpTools.MaxResponseBytes / 2) };
        var huge = await tools.CallAsync("diagnostics", null, TestContext.Current.CancellationToken);
        Assert.True(huge.IsError);
        Assert.Null(huge.StructuredContent);
        backend.Result = new() { ["healthy"] = true };
        var small = await tools.CallAsync("diagnostics", null, TestContext.Current.CancellationToken);
        Assert.NotEqual(true, small.IsError);
        Assert.NotNull(small.StructuredContent);
    }

    [Fact]
    public void EveryToolHasExplicitInputAndOutputSchemaAndDownloadDeclaresWrites()
    {
        foreach (var tool in McpTools.Catalog)
        {
            Assert.Equal(McpSchemas.Dialect, tool.InputSchema.GetProperty("$schema").GetString());
            Assert.False(tool.InputSchema.GetProperty("additionalProperties").GetBoolean());
            Assert.True(tool.OutputSchema!.Value.GetProperty("properties").EnumerateObject().Any());
            Assert.True(tool.OutputSchema.Value.GetProperty("required").GetArrayLength() > 0);
            Assert.Equal(tool.Name != "attachment_download", tool.Annotations!.ReadOnlyHint);
        }
    }

    [Fact]
    public void MessagePagesAdvanceAcrossFilteringAndUseSearchContinuationVerbatim()
    {
        static TdApi.Message Message(long id) => new() { Id = id, ChatId = -1001,
            Content = new TdApi.MessageContent.MessageText { Text = new TdApi.FormattedText { Text = "hello", Entities = [] } } };
        var page = McpMessagePage.From([Message(300), Message(200), Message(200)], null);
        Assert.Equal(new long[] { 300, 200 }, page.Messages.Select(m => m.Id));
        Assert.Empty(ChatCommands.FilterMessages(page.Messages, false, "video"));
        Assert.Equal(199, page.Next);
        Assert.True(page.HasMore);
        var next = McpMessagePage.From([Message(100)], page.Next);
        Assert.Empty(next.Messages.Select(m => m.Id).Intersect(page.Messages.Select(m => m.Id)));
        Assert.Equal(99, next.Next);
        Assert.False(McpMessagePage.From([], next.Next).HasMore);
        Assert.Equal(150, McpMessagePage.From(page.Messages, null, 150).Next);
        Assert.True(McpMessagePage.From([], 150, 100).HasMore);
        Assert.False(McpMessagePage.From([Message(100)], 150, 0).HasMore);
        Assert.Throws<InvalidOperationException>(() => McpMessagePage.From([Message(300)], 199));
    }

    private sealed class Backend : IMcpTelegramBackend
    {
        internal JsonObject Result = new();
        public Task<JsonObject> ExecuteAsync(string name, McpArguments arguments, CancellationToken cancellationToken) => Task.FromResult(Result);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
