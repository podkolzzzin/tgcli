using Newtonsoft.Json.Linq;
using TdLib;
using Xunit;

namespace TgCli.Tests;

public sealed class ExportTests
{
    [Fact]
    public void ManifestContainsCompletenessEvidence()
    {
        var history = new HistoryFetchResult
        {
            PagesFetched = 3,
            Duplicates = 2,
            Retries = 1,
            Complete = false,
            TerminationReason = "max_pages_reached"
        };
        history.SourceChats.Add(-1001);
        history.PagesByChat[-1001] = 3;
        history.OldestReachableByChat[-1001] = 42;
        history.Messages.Add(new ExportedMessage(
            new TdApi.Message { ChatId = -1001, Id = 42, Date = 1_700_000_000 },
            -1001, -1001, 42));

        var manifest = ChatHistory.BuildManifest(history);

        Assert.Equal(1, manifest.Value<int>("message_count"));
        Assert.Equal("max_pages_reached", manifest.Value<string>("termination_reason"));
        Assert.False(manifest.Value<bool>("complete"));
        Assert.Equal(3, manifest.Value<int>("pages_fetched"));
        Assert.NotNull(manifest["oldest_reachable_message_by_chat"]);
        Assert.NotNull(manifest["newest_fetched_message"]);
    }

    [Fact]
    public void TombstoneUsesVersionedSchema()
    {
        var value = ExportSchema.Tombstone(10, 20);

        Assert.Equal("tgcli.message", value.Value<string>("schema"));
        Assert.Equal("4.0", value.Value<string>("schema_version"));
        Assert.True(value.Value<bool>("is_deleted"));
        Assert.Equal(10, value.Value<long>("chat_id"));
        Assert.Equal(20, value.Value<long>("message_id"));
    }

    [Fact]
    public void AttachmentMetadataIncludesDuration()
    {
        var content = new TdApi.MessageContent.MessageVoiceNote
        {
            VoiceNote = new TdApi.VoiceNote
            {
                Duration = 12,
                Voice = new TdApi.File
                {
                    Id = 7,
                    Size = 99,
                    Remote = new TdApi.RemoteFile { Id = "remote", UniqueId = "unique" }
                }
            }
        };

        var attachment = MessageFiles.GetPrimaryAttachment(content);

        Assert.NotNull(attachment);
        Assert.Equal("voice", attachment.Kind);
        Assert.Equal(12, attachment.Duration);
        Assert.Equal(99, attachment.Size);
    }
}
