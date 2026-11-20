#nullable enable
using System.IO;
using Newtonsoft.Json.Linq;
using TdLib;
using Xunit;

namespace TgCli.Tests;

public sealed class CliWireTests
{
    [Fact]
    public void TombstoneKeepsExactIdentityInSchemaEight()
    {
        var row = ExportSchema.Tombstone(-1000000000027, 9007199254740993);
        Assert.Equal("8.0", row.Value<string>("schema_version"));
        Assert.Equal(JTokenType.String, row["chat_id"]!.Type);
        Assert.Equal(JTokenType.String, row["message_id"]!.Type);
        Assert.Equal("9007199254740993", row.Value<string>("message_id"));
    }

    [Fact]
    public void AttachmentUsesDecimalFileIdAndNumericSize()
    {
        var message = new TdApi.Message
        {
            Content = new TdApi.MessageContent.MessageDocument
            {
                Document = new TdApi.Document { Document_ = new TdApi.File { Id = 42, Size = 123 } }
            }
        };
        var row = ExportSchema.ToAttachmentJson(Assert.Single(MessageFiles.GetAllFiles(message)), "caption", null);
        Assert.Equal(JTokenType.String, row["file_id"]!.Type);
        Assert.Equal("42", row.Value<string>("file_id"));
        Assert.Equal(JTokenType.Integer, row["size"]!.Type);
    }

    [Fact]
    public void DownloadReceiptDoesNotRoundAnyIdentifierOrStringifySize()
    {
        var row = BatchDownloads.ToJson(new BatchDownloadResult(0, -1000000000027, 9007199254740993, 42,
            "attachment.bin", null, SizeBytes: 1234));
        Assert.Equal(JTokenType.String, row["chat_id"]!.Type);
        Assert.Equal(JTokenType.String, row["message_id"]!.Type);
        Assert.Equal(JTokenType.String, row["file_id"]!.Type);
        Assert.Equal(JTokenType.Integer, row["size_bytes"]!.Type);
    }

    [Fact]
    public void MetricIdentifiersAndTimestampUseTheirWireTypes()
    {
        var row = ChannelCommands.ToMetricRow(new TdApi.Message { ChatId = -1000000000027, Id = 42L << 20, Date = 2 });
        Assert.Equal(JTokenType.String, row["chat_id"]!.Type);
        Assert.Equal(JTokenType.String, row["message_id"]!.Type);
        Assert.Equal(JTokenType.String, row["post_id"]!.Type);
        Assert.Equal("42", row.Value<string>("post_id"));
        Assert.Equal("1970-01-01T00:00:02.0000000Z", row.Value<string>("date"));
        Assert.Equal(JTokenType.Null, row["view_count"]!.Type);
    }

    [Theory]
    [InlineData("json")]
    [InlineData("plain")]
    public void ChatRendererEmitsRowsForEveryAdvertisedFormat(string formatName)
    {
        var format = Output.ParseFormat(formatName);
        using var stdout = new StringWriter();
        Output.PrintChats([new(9007199254740993, "Joined", "private", null, 2, null)], format, stdout);
        Assert.Contains("Joined", stdout.ToString());
        if (format == OutputFormat.Json)
        {
            var chat = Assert.Single(JArray.Parse(stdout.ToString()));
            Assert.Equal(JTokenType.String, chat["chat_id"]!.Type);
            Assert.Equal(JTokenType.Null, chat["username"]!.Type);
        }
    }
}
