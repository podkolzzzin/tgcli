using Newtonsoft.Json.Linq;
using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
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
        history.OldestReachableByChat[-1001] = ChatHistory.ToChannelMessageId(42);
        history.Messages.Add(new ExportedMessage(
            new TdApi.Message { ChatId = -1001, Id = ChatHistory.ToChannelMessageId(42), Date = 1_700_000_000 },
            -1001, -1001, ChatHistory.ToChannelMessageId(42)));

        var manifest = ChatHistory.BuildManifest(history);

        Assert.Equal(1, manifest.Value<int>("message_count"));
        Assert.Equal(1, manifest.Value<int>("fetched_count"));
        Assert.Equal("max_pages_reached", manifest.Value<string>("termination_reason"));
        Assert.False(manifest.Value<bool>("complete"));
        Assert.Equal(3, manifest.Value<int>("pages_fetched"));
        Assert.NotNull(manifest["oldest_reachable_message_by_chat"]);
        Assert.NotNull(manifest["newest_fetched_message"]);
        Assert.Equal(0, manifest["public_id_range"]!.Value<int>("missing_count"));
    }

    [Fact]
    public void TombstoneUsesVersionedSchema()
    {
        var value = ExportSchema.Tombstone(10, 20);

        Assert.Equal("tgcli.message", value.Value<string>("schema"));
        Assert.Equal("5.0", value.Value<string>("schema_version"));
        Assert.True(value.Value<bool>("is_deleted"));
        Assert.Equal(10, value.Value<long>("chat_id"));
        Assert.Equal(20, value.Value<long>("message_id"));
    }

    [Fact]
    public void ChannelMessageIdsRoundTripThroughPublicIds()
    {
        var messageId = ChatHistory.ToChannelMessageId(575);

        Assert.Equal(602931200, messageId);
        Assert.Equal(575, ChatHistory.GetShortMessageId(messageId));
    }

    [Fact]
    public void ManifestReportsChannelPublicIdGaps()
    {
        var history = new HistoryFetchResult { Complete = true };
        history.SourceChats.Add(-1001);
        history.Messages.Add(new ExportedMessage(new TdApi.Message { ChatId = -1001, Id = ChatHistory.ToChannelMessageId(1), Date = 100 }, -1001, -1001, ChatHistory.ToChannelMessageId(1)));
        history.Messages.Add(new ExportedMessage(new TdApi.Message { ChatId = -1001, Id = ChatHistory.ToChannelMessageId(3), Date = 300 }, -1001, -1001, ChatHistory.ToChannelMessageId(3)));

        var manifest = ChatHistory.BuildManifest(history);
        var range = manifest["public_id_range"]!;

        Assert.Equal(1, range.Value<long>("first"));
        Assert.Equal(3, range.Value<long>("last"));
        Assert.Equal(1, range.Value<int>("missing_count"));
        Assert.Equal(2, range["missing_public_ids"]![0]!.Value<long>());
        Assert.Equal(1, manifest["first_post"]!.Value<long>("public_id"));
        Assert.Equal(3, manifest["last_post"]!.Value<long>("public_id"));
    }

    [Fact]
    public void MetricsFlattenMessageInteractionInfo()
    {
        var metrics = ExportSchema.ToMetricsJson(new TdApi.MessageInteractionInfo
        {
            ViewCount = 120,
            ForwardCount = 7,
            ReplyInfo = new TdApi.MessageReplyInfo { ReplyCount = 3 },
            Reactions = new TdApi.MessageReactions
            {
                Reactions =
                [
                    new TdApi.MessageReaction
                    {
                        Type = new TdApi.ReactionType.ReactionTypeEmoji { Emoji = "\ud83d\udc4d" },
                        TotalCount = 5,
                        IsChosen = true
                    }
                ],
                PaidReactors = [new TdApi.PaidReactor { StarCount = 2 }]
            }
        });

        Assert.Equal(120, metrics.Value<int>("view_count"));
        Assert.Equal(7, metrics.Value<int>("forward_count"));
        Assert.Equal(3, metrics.Value<int>("reply_count"));
        Assert.True(metrics.Value<bool>("has_comments"));
        Assert.Equal(2, metrics.Value<long>("paid_reaction_count"));
        Assert.Equal("emoji", metrics["reaction_counts"]![0]!.Value<string>("type"));
        Assert.Equal("\ud83d\udc4d", metrics["reaction_counts"]![0]!.Value<string>("emoji"));
        Assert.Equal(5, metrics["reaction_counts"]![0]!.Value<int>("count"));
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

    [Fact]
    public void LightweightJsonRowsUseSnakeCaseNames()
    {
        var chatJson = JsonSerializer.Serialize(new ChatRow(1, "title", "private", "user", 0, "last"));
        var messageJson = JsonSerializer.Serialize(new MessageRow(1, 2, "date", "sender", "name", "text", 3, "body", 4));
        var exportedMessageJson = JsonSerializer.Serialize(Output.ToJsonRow(new MessageRow(1, 2, "date", "sender", "name", "text", 3, "body", 4), includeLinks: false));

        Assert.Contains("\"chat_id\":1", chatJson);
        Assert.Contains("\"last_message\":\"last\"", chatJson);
        Assert.DoesNotContain("ChatId", chatJson);

        Assert.Contains("\"message_id\":2", messageJson);
        Assert.Contains("\"reply_to_message_id\":4", messageJson);
        Assert.DoesNotContain("MessageId", messageJson);

        Assert.Contains("\"chat_id\":1", exportedMessageJson);
        Assert.Contains("\"message_id\":2", exportedMessageJson);
        Assert.DoesNotContain("ChatId", exportedMessageJson);
    }

    [Fact]
    public void DownloadDestinationTreatsMissingExtensionlessPathAsDirectory()
    {
        var basePath = Path.Combine(Path.GetTempPath(), "tgcli-test-" + Guid.NewGuid().ToString("N"), "assets");
        var file = new TdApi.File
        {
            Id = 1254,
            Local = new TdApi.LocalFile { Path = "/tmp/5318883015880545858_120.jpg" }
        };

        var destination = TgCommands.ResolveDownloadDestination(basePath, file);

        Assert.Equal(Path.Combine(basePath, "5318883015880545858_120.jpg"), destination);
    }

    [Fact]
    public async Task AttachmentIndexCanRoundTripFileMetadata()
    {
        var session = Path.Combine(Path.GetTempPath(), "tgcli-cache-" + Guid.NewGuid().ToString("N"));
        var file = new MessageFile("photo", 1254, "remote-1", "unique-1", null, null, null, null, null, 24156, "content.photo.sizes[].photo", new TdApi.File { Id = 1254 });

        await AttachmentIndex.RecordAsync(session, 1, 2, [file]);
        var resolved = await AttachmentIndex.ResolveAsync(session, 1254);

        Assert.NotNull(resolved);
        Assert.Equal(1, resolved!.chat_id);
        Assert.Equal(2, resolved.message_id);
        Assert.Equal(1254, resolved.file_id);
        Assert.Equal("remote-1", resolved.remote_id);
    }
}
