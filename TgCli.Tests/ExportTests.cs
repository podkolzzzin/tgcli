using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
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
    public void VideoAttachmentStatisticsReportMessageAndUniqueTotals()
    {
        static TdApi.Message Video(long messageId, int fileId, long size, string uniqueId)
        {
            return new TdApi.Message
            {
                Id = messageId,
                Content = new TdApi.MessageContent.MessageVideo
                {
                    Video = new TdApi.Video
                    {
                        Video_ = new TdApi.File
                        {
                            Id = fileId,
                            Size = size,
                            Remote = new TdApi.RemoteFile { Id = $"remote-{fileId}", UniqueId = uniqueId }
                        }
                    }
                }
            };
        }

        var videoNote = new TdApi.Message
        {
            Id = 3,
            Content = new TdApi.MessageContent.MessageVideoNote
            {
                VideoNote = new TdApi.VideoNote
                {
                    Video = new TdApi.File { Id = 3, Size = 400 }
                }
            }
        };

        var summary = AttachmentStatistics.Build(
            [Video(1, 1, 100, "same-video"), Video(2, 2, 200, "same-video"), videoNote],
            "video");

        Assert.Equal("video", summary.Value<string>("type"));
        Assert.Equal(2, summary.Value<int>("count"));
        Assert.Equal(300, summary.Value<long>("total_size_bytes"));
        Assert.Equal(1, summary.Value<int>("unique_count"));
        Assert.Equal(100, summary.Value<long>("unique_total_size_bytes"));
        Assert.Equal(0, summary.Value<int>("unknown_size_count"));
    }

    [Fact]
    public void AttachmentStatisticsFallBackToExpectedSize()
    {
        var message = new TdApi.Message
        {
            Id = 1,
            Content = new TdApi.MessageContent.MessageVideo
            {
                Video = new TdApi.Video
                {
                    Video_ = new TdApi.File
                    {
                        Id = 1,
                        Size = 0,
                        ExpectedSize = 1234
                    }
                }
            }
        };

        var summary = AttachmentStatistics.Build([message], "video");

        Assert.Equal(1234, summary.Value<long>("total_size_bytes"));
        Assert.Equal(1, summary.Value<int>("known_size_count"));
    }

    [Fact]
    public async Task ForumTopicHistoryPaginatesToAnExactBoundary()
    {
        var requests = new List<TdApi.GetForumTopicHistory>();
        var pages = new Queue<TdApi.Messages>(
        [
            new TdApi.Messages
            {
                Messages_ = Enumerable.Range(101, 100)
                    .Reverse()
                    .Select(id => new TdApi.Message { Id = id })
                    .ToArray()
            },
            new TdApi.Messages
            {
                Messages_ = Enumerable.Range(51, 50)
                    .Reverse()
                    .Select(id => new TdApi.Message { Id = id })
                    .ToArray()
            }
        ]);

        var result = await ForumTopicHistory.FetchAsync(
            chatId: -1001,
            topicId: 10,
            fromMessageId: 0,
            offset: 0,
            limit: 100,
            all: true,
            maxPages: 10,
            (request, _) =>
            {
                requests.Add(request);
                return Task.FromResult(pages.Dequeue());
            },
            CancellationToken.None);

        Assert.True(result.Complete);
        Assert.Equal("short_page", result.TerminationReason);
        Assert.Equal(2, result.PagesFetched);
        Assert.Equal(150, result.Messages.Count);
        Assert.Equal(101, requests[1].FromMessageId);
        Assert.All(requests, request => Assert.Equal(10, request.ForumTopicId));
    }

    [Fact]
    public async Task ForumTopicHistoryRejectsAStalledCursor()
    {
        var messages = Enumerable.Range(901, 100)
            .Reverse()
            .Select(id => new TdApi.Message { Id = id })
            .ToArray();
        var result = await ForumTopicHistory.FetchAsync(
            chatId: -1001,
            topicId: 10,
            fromMessageId: 0,
            offset: 0,
            limit: 100,
            all: true,
            maxPages: 10,
            (_, _) => Task.FromResult(new TdApi.Messages { Messages_ = messages }),
            CancellationToken.None);

        Assert.False(result.Complete);
        Assert.Equal("cursor_stalled", result.TerminationReason);
        Assert.Equal(2, result.PagesFetched);
    }

    [Fact]
    public async Task ForumTopicHistoryReportsMaxPageTruncation()
    {
        var result = await ForumTopicHistory.FetchAsync(
            chatId: -1001,
            topicId: 10,
            fromMessageId: 0,
            offset: 0,
            limit: 100,
            all: true,
            maxPages: 1,
            (_, _) => Task.FromResult(new TdApi.Messages
            {
                Messages_ = Enumerable.Range(1, 100)
                    .Select(id => new TdApi.Message { Id = id })
                    .ToArray()
            }),
            CancellationToken.None);

        Assert.False(result.Complete);
        Assert.Equal("max_pages_reached", result.TerminationReason);
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

    [Fact]
    public void SessionSecretContainsOnlyAuthStateFiles()
    {
        var session = Path.Combine(Path.GetTempPath(), "tgcli-session-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(session, "tdlib-db", "thumbnails"));
        Directory.CreateDirectory(Path.Combine(session, "tdlib-files"));
        Directory.CreateDirectory(Path.Combine(session, "app"));
        File.WriteAllText(Path.Combine(session, "config.json"), """{"apiId":1,"apiHash":"hash"}""");
        File.WriteAllText(Path.Combine(session, "tdlib-db", "td.binlog"), "auth-state");
        File.WriteAllText(Path.Combine(session, "tdlib-db", "db.sqlite"), "message-cache");
        File.WriteAllText(Path.Combine(session, "tdlib-db", "thumbnails", "1.webp"), "thumbnail");
        File.WriteAllText(Path.Combine(session, "tdlib-files", "photo.jpg"), "image");
        File.WriteAllText(Path.Combine(session, "attachment-index.jsonl"), "{}");
        File.WriteAllText(Path.Combine(session, "tdlib.lock"), "123");

        var secret = SessionSecret.Export(session);
        var files = SessionSecret.Inspect(secret);

        Assert.Equal(["config.json", "manifest.json", "tdlib-db/td.binlog"], files.Keys.OrderBy(x => x, StringComparer.Ordinal).ToArray());
        Assert.Equal("""{"apiId":1,"apiHash":"hash"}""", System.Text.Encoding.UTF8.GetString(files["config.json"]));
        Assert.Equal("auth-state", System.Text.Encoding.UTF8.GetString(files["tdlib-db/td.binlog"]));
    }

    [Fact]
    public void SessionSecretImportRefusesToOverwriteWithoutForce()
    {
        var source = Path.Combine(Path.GetTempPath(), "tgcli-session-source-" + Guid.NewGuid().ToString("N"));
        var target = Path.Combine(Path.GetTempPath(), "tgcli-session-target-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(source, "tdlib-db"));
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(source, "config.json"), """{"apiId":1,"apiHash":"hash"}""");
        File.WriteAllText(Path.Combine(source, "tdlib-db", "td.binlog"), "auth-state");
        File.WriteAllText(Path.Combine(target, "config.json"), "existing");
        var secret = SessionSecret.Export(source);

        Assert.Throws<System.ComponentModel.DataAnnotations.ValidationException>(() => SessionSecret.Import(secret, target, force: false));

        SessionSecret.Import(secret, target, force: true);

        Assert.Equal("""{"apiId":1,"apiHash":"hash"}""", File.ReadAllText(Path.Combine(target, "config.json")));
        Assert.Equal("auth-state", File.ReadAllText(Path.Combine(target, "tdlib-db", "td.binlog")));
        Assert.False(File.Exists(Path.Combine(target, "tdlib-db", "db.sqlite")));
    }

    [Fact]
    public void SessionLockCanRepairStaleOwnerMetadata()
    {
        var session = Path.Combine(Path.GetTempPath(), "tgcli-lock-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(session);
        File.WriteAllText(Path.Combine(session, "tdlib.lock"), string.Empty);
        File.WriteAllText(Path.Combine(session, "tdlib.lock.owner"), "12345");

        var before = SessionLock.Inspect(session);
        var after = SessionLock.RepairStale(session);

        Assert.False(before.locked);
        Assert.True(before.owner_metadata_stale);
        Assert.Equal("12345", before.owner);
        Assert.False(after.locked);
        Assert.False(after.owner_metadata_stale);
        Assert.Null(after.owner);
    }

    [Fact]
    public void SessionLockRefusesToRepairAnActiveOwner()
    {
        var session = Path.Combine(Path.GetTempPath(), "tgcli-lock-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(session);
        var lockPath = Path.Combine(session, "tdlib.lock");
        File.WriteAllText(Path.Combine(session, "tdlib.lock.owner"), "54321");
        using var held = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

        var status = SessionLock.Inspect(session);

        Assert.True(status.locked);
        Assert.Equal("54321", status.owner);
        Assert.Throws<IOException>(() => SessionLock.RepairStale(session));
    }
}
