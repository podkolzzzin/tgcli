#nullable enable

using System;
using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TdLib;
using Xunit;

namespace TgCli.Tests;

public sealed class MediaFlowTests
{
    private static TdApi.Message VideoMessage() => new()
    {
        ChatId = -1001,
        Id = 2097152,
        Content = new TdApi.MessageContent.MessageVideo
        {
            Caption = new TdApi.FormattedText { Text = "original caption" },
            Video = new TdApi.Video
            {
                Video_ = new TdApi.File { Id = 10, Size = 1200 },
                FileName = "lecture.mp4",
                MimeType = "video/mp4",
                Width = 1920,
                Height = 1080,
                Duration = 42,
                Thumbnail = new TdApi.Thumbnail
                {
                    File = new TdApi.File { Id = 11, ExpectedSize = 40 },
                    Width = 90,
                    Height = 50,
                    Format = new TdApi.ThumbnailFormat.ThumbnailFormatJpeg()
                }
            }
        }
    };

    [Fact]
    public void ExportAttachmentPreservesVideoAndThumbnailMetadata()
    {
        var attachments = MessageFiles.GetAllFiles(VideoMessage())
            .ToDictionary(f => f.FileId, f => ExportSchema.ToAttachmentJson(f, "original caption", null));

        Assert.Equal(2, attachments.Count);
        var primary = attachments[10];
        Assert.Equal("video", (string?)primary["kind"]);
        Assert.Equal("primary", (string?)primary["role"]);
        Assert.Equal("lecture.mp4", (string?)primary["file_name"]);
        Assert.Equal("video/mp4", (string?)primary["mime_type"]);
        Assert.Equal(1920, (int?)primary["width"]);
        Assert.Equal(1080, (int?)primary["height"]);
        Assert.Equal(42, (int?)primary["duration"]);

        var thumbnail = attachments[11];
        Assert.Equal("thumbnail", (string?)thumbnail["kind"]);
        Assert.Equal("thumbnail", (string?)thumbnail["role"]);
        Assert.Equal("image/jpeg", (string?)thumbnail["mime_type"]);
        Assert.Equal(90, (int?)thumbnail["width"]);
        Assert.Equal(50, (int?)thumbnail["height"]);
        Assert.Equal(40L, (long?)thumbnail["size"]);
    }

    [Theory]
    [InlineData("video", "file")]
    [InlineData("voice", "voice-note")]
    [InlineData(null, "all")]
    public void BatchRejectsRequestsThatCanShareOneDestination(string? first, string second)
    {
        var firstType = first is null ? "" : $",\"type\":\"{first}\"";
        using var reader = new StringReader(
            $"{{\"chat_id\":1,\"message_id\":2{firstType}}}\n" +
            $"{{\"chat_id\":1,\"message_id\":2,\"type\":\"{second}\"}}");
        Assert.Throws<ValidationException>(() => BatchDownloads.Read(reader, null));
    }

    [Fact]
    public void BatchConsumesLosslessDecimalStringIds()
    {
        using var reader = new StringReader("""{"chat_id":"-1001","message_id":"9007199254740993","type":"GIF"}""");
        var item = Assert.Single(BatchDownloads.Read(reader, null));
        Assert.Equal((-1001L, 9007199254740993L, "animation"), (item.ChatId, item.MessageId, item.Type));
    }

    [Fact]
    public async Task AdvisoryIndexFailureDoesNotPreventMessageInspection()
    {
        var directory = Directory.CreateTempSubdirectory("tgcli-media-index-");
        try
        {
            Directory.CreateDirectory(Path.Combine(directory.FullName, "attachment-index.jsonl"));
            await AttachmentIndex.RecordAsync(directory.FullName, -1001, 2097152, MessageFiles.GetAllFiles(VideoMessage()));
            Assert.Null(await AttachmentIndex.ResolveAsync(directory.FullName, 10));
        }
        finally
        {
            directory.Delete(true);
        }
    }

    [Fact]
    public async Task IndexedNumericThumbnailWithoutRemoteIdResolvesExactly()
    {
        var directory = Directory.CreateTempSubdirectory("tgcli-media-index-");
        try
        {
            var message = VideoMessage();
            await AttachmentIndex.RecordAsync(directory.FullName, message.ChatId, message.Id, MessageFiles.GetAllFiles(message));
            var client = new FakeTdClient(request => request switch
            {
                TdApi.GetMessage get when get.ChatId == message.ChatId && get.MessageId == message.Id => message,
                _ => throw new InvalidOperationException("Unexpected Telegram request.")
            });

            var file = await TgCommands.ResolveDownloadFileAsync(client, directory.FullName, "file", "11", 0, 0, TestContext.Current.CancellationToken);
            Assert.Equal(11, file.Id);
        }
        finally
        {
            directory.Delete(true);
        }
    }

    [Fact]
    public async Task IndexedNumericAttachmentCannotBecomeAnEditedReplacement()
    {
        var directory = Directory.CreateTempSubdirectory("tgcli-media-index-");
        try
        {
            var message = VideoMessage();
            await AttachmentIndex.RecordAsync(directory.FullName, message.ChatId, message.Id, MessageFiles.GetAllFiles(message));
            ((TdApi.MessageContent.MessageVideo)message.Content).Video.Thumbnail.File.Id = 12;
            var client = new FakeTdClient(request => request switch
            {
                TdApi.GetMessage get when get.ChatId == message.ChatId && get.MessageId == message.Id => message,
                _ => throw new InvalidOperationException("Unexpected Telegram request.")
            });

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                TgCommands.ResolveDownloadFileAsync(client, directory.FullName, "file", "11", 0, 0, TestContext.Current.CancellationToken));
        }
        finally
        {
            directory.Delete(true);
        }
    }

    [Fact]
    public async Task CompletedDownloadIsRequiredBeforeReplacingDestination()
    {
        var directory = Directory.CreateTempSubdirectory("tgcli-media-copy-");
        try
        {
            var source = Path.Combine(directory.FullName, "partial");
            var destination = Path.Combine(directory.FullName, "existing");
            File.WriteAllText(source, "partial");
            File.WriteAllText(destination, "previous output");
            var file = new TdApi.File { Id = 11, Local = new() { Path = source, IsDownloadingCompleted = false } };

            await Assert.ThrowsAsync<IOException>(() => TgCommands.CopyDownloadedFileAsync(file, destination, TestContext.Current.CancellationToken));
            Assert.Equal("previous output", File.ReadAllText(destination));
        }
        finally
        {
            directory.Delete(true);
        }
    }

    [Fact]
    public async Task CancellationPreservesPreviousDestination()
    {
        var directory = Directory.CreateTempSubdirectory("tgcli-media-copy-");
        try
        {
            var source = Path.Combine(directory.FullName, "complete");
            var destination = Path.Combine(directory.FullName, "existing");
            File.WriteAllText(source, "replacement");
            File.WriteAllText(destination, "previous output");
            var file = new TdApi.File { Id = 11, Size = 11, Local = new() { Path = source, IsDownloadingCompleted = true } };
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                TgCommands.CopyDownloadedFileAsync(file, destination, cancellation.Token));
            Assert.Equal("previous output", File.ReadAllText(destination));
            Assert.Equal(2, Directory.GetFiles(directory.FullName).Length);
        }
        finally
        {
            directory.Delete(true);
        }
    }

    private sealed class FakeTdClient(Func<TdApi.Object, TdApi.Object> reply) : TdApi.IClient
    {
        public event EventHandler<TdApi.Update>? UpdateReceived { add { } remove { } }
        public void Send<TResult>(TdApi.Function<TResult> function) => throw new NotSupportedException();
        public TResult Execute<TResult>(TdApi.Function<TResult> function) where TResult : TdApi.Object => (TResult)reply(function);
        public Task<TResult> ExecuteAsync<TResult>(TdApi.Function<TResult> function) where TResult : TdApi.Object =>
            Task.FromResult(Execute(function));
    }
}
