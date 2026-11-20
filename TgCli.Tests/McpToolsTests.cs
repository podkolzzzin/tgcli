using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
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
    public void AdvertisedSchemaUsesDecimalIdentifiersAndRichMessageVersion()
    {
        var topics = McpTools.Catalog.Single(tool => tool.Name == "forum_topics").OutputSchema!.Value;
        var topicFields = topics.GetProperty("properties").GetProperty("items").GetProperty("items").GetProperty("properties");
        Assert.Equal("string", topicFields.GetProperty("topic_id").GetProperty("type").GetString());

        var output = McpTools.Catalog.Single(tool => tool.Name == "message_get").OutputSchema!.Value;
        var message = output.GetProperty("properties");
        Assert.Equal("8.0", message.GetProperty("schema_version").GetProperty("const").GetString());
        Assert.Contains(output.GetProperty("required").EnumerateArray(), name => name.GetString() == "topic_id");
        Assert.Equal(["string", "null"], message.GetProperty("topic_id").GetProperty("type")
            .EnumerateArray().Select(type => type.GetString()));
        Assert.Equal("string", message.GetProperty("attachments").GetProperty("items")
            .GetProperty("properties").GetProperty("file_id").GetProperty("type").GetString());
    }

    private sealed class Backend : IMcpTelegramBackend
    {
        internal JsonObject Result = new();
        public Task<JsonObject> ExecuteAsync(string name, McpArguments arguments, CancellationToken cancellationToken) => Task.FromResult(Result);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
