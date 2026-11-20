#nullable enable

using System;
using System.Collections.Concurrent;
using System.Linq;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using TdLib;
using Xunit;

namespace TgCli.Tests;

public sealed class BatchRecoveryTests
{
    [Fact]
    public async Task RestartReusesVerifiedFilesAndRecopiesCorruptFiles()
    {
        var directory = Directory.CreateTempSubdirectory("tgcli-media-batch-");
        var token = TestContext.Current.CancellationToken;
        try
        {
            var request = new BatchDownloadRequest(0, 1001, 1048576, "video");
            var source = MakeFile(directory, 10, "replacement");
            Task<TdApi.File> Resolve(BatchDownloadRequest _, CancellationToken __) => Task.FromResult(source);
            Task<TdApi.File> Complete(BatchDownloadRequest _, TdApi.File file, CancellationToken __) => Task.FromResult(file);

            var first = Assert.Single(await BatchDownloads.RunAsync([request], directory.FullName, 1, Resolve, Complete, token));
            Assert.True(first.Ok);
            Assert.Equal("replacement", File.ReadAllText(first.Path!));

            var skipped = Assert.Single(await BatchDownloads.RunAsync([request], directory.FullName, 1, Resolve,
                (_, _, _) => throw new IOException("unexpected new download"), token));
            Assert.True(skipped.Ok, skipped.Error);
            Assert.Equal("skipped", (string?)BatchDownloads.ToJson(skipped)["status"]);
            Assert.Equal(first.Path, skipped.Path);

            File.WriteAllText(first.Path!, "corrupt!!!!"); // Same size as "replacement".
            var recovered = Assert.Single(await BatchDownloads.RunAsync([request], directory.FullName, 1, Resolve, Complete, token));
            Assert.True(recovered.Ok);
            Assert.Equal("downloaded", (string?)BatchDownloads.ToJson(recovered)["status"]);
            Assert.Equal("replacement", File.ReadAllText(recovered.Path!));
        }
        finally
        {
            directory.Delete(true);
        }
    }

    [Fact]
    public async Task CancellationReturnsCompletedAndUnstartedReceipts()
    {
        var directory = Directory.CreateTempSubdirectory("tgcli-media-batch-");
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        try
        {
            BatchDownloadRequest[] requests =
            [
                new(0, 1001, 1048576, "video"),
                new(1, 1001, 2097152, "video"),
                new(2, 1001, 3145728, "video")
            ];
            var source = MakeFile(directory, 10, "video content");
            var result = await BatchDownloads.RunAsync(requests, directory.FullName, 1,
                (request, token) =>
                {
                    if (request.Index == 1) cancellation.Cancel();
                    token.ThrowIfCancellationRequested();
                    return Task.FromResult(source);
                },
                (_, file, _) => Task.FromResult(file), cancellation.Token);

            Assert.Collection(result,
                item => Assert.Equal("downloaded", (string?)BatchDownloads.ToJson(item)["status"]),
                item => Assert.Equal("canceled", (string?)BatchDownloads.ToJson(item)["status"]),
                item => Assert.Equal("canceled", (string?)BatchDownloads.ToJson(item)["status"]));
            Assert.Equal("video content", File.ReadAllText(result[0].Path!));
        }
        finally
        {
            directory.Delete(true);
        }
    }

    [Fact]
    public async Task FailedBatchFileKeepsKnownIdentityAndPreviousDestination()
    {
        var directory = Directory.CreateTempSubdirectory("tgcli-media-batch-");
        try
        {
            var request = new BatchDownloadRequest(0, 1001, 1048576, "video");
            var file = MakeFile(directory, 10, "partial");
            var destination = BatchDownloads.ResolveDestination(directory.FullName, request, file);
            File.WriteAllText(destination, "prior full file");
            file.Local.IsDownloadingCompleted = false;

            var item = Assert.Single(await BatchDownloads.RunAsync([request], directory.FullName, 1,
                (_, _) => Task.FromResult(file),
                (_, selected, _) => Task.FromResult(selected), TestContext.Current.CancellationToken));
            Assert.False(item.Ok);
            Assert.Equal(10, item.FileId);
            Assert.Equal(destination, item.Path);
            Assert.Equal("prior full file", File.ReadAllText(destination));
        }
        finally
        {
            directory.Delete(true);
        }
    }

    [Fact]
    public async Task EquivalentSelectorsShareOneDownloadAndExactAttachmentsKeepDistinctReceipts()
    {
        var workspace = Directory.CreateTempSubdirectory("tgcli-media-multi-");
        try
        {
            var output = workspace.CreateSubdirectory("out").FullName;
            using var input = new StringReader("""
                {"chat_id":"1001","message_id":"1048576","type":"document"}
                {"chat_id":"1001","message_id":"1048576","type":"doc"}
                {"chat_id":"1001","message_id":"1048576","file_id":"10"}
                {"chat_id":"1001","message_id":"1048576","attachment_id":"11"}
                {"chat_id":"1001","message_id":"2097152","type":"video"}
                """);
            var requests = BatchDownloads.Read(input, null);
            var primary = MakeFile(workspace.CreateSubdirectory("first"), 10, "document bytes");
            var thumbnail = MakeFile(workspace.CreateSubdirectory("second"), 11, "thumbnail bytes");
            var calls = new ConcurrentDictionary<int, int>();
            Task<TdApi.File> Resolve(BatchDownloadRequest request, CancellationToken token)
            {
                if (request.MessageId == 2097152)
                    throw new IOException("Synthetic inaccessible message.");
                return Task.FromResult(request.AttachmentId == "11" ? thumbnail : primary);
            }

            var ct = TestContext.Current.CancellationToken;
            var result = await BatchDownloads.RunAsync(requests, output, 4, Resolve, (_, file, _) =>
            {
                calls.AddOrUpdate(file.Id, 1, (_, count) => count + 1);
                return Task.FromResult(file);
            }, ct);
            Assert.Equal(new[] { 0, 1, 2, 3, 4 }, result.Select(item => item.Index));
            Assert.All(result.Take(4), item => Assert.True(item.Ok, item.Error));
            Assert.False(result[4].Ok);
            Assert.Equal(result[0].Path, result[1].Path);
            Assert.Equal(result[0].Path, result[2].Path);
            Assert.NotEqual(result[0].Path, result[3].Path);
            Assert.Equal("document bytes", File.ReadAllText(result[0].Path!));
            Assert.Equal("thumbnail bytes", File.ReadAllText(result[3].Path!));
            Assert.Equal(1, calls[10]);
            Assert.Equal(1, calls[11]);

            var retry = await BatchDownloads.RunAsync(requests, output, 4, Resolve,
                (_, _, _) => throw new IOException("Expected verified reuse."), ct);
            Assert.All(retry.Take(4), item => Assert.Equal("skipped", item.Status));
            Assert.Equal(result[3].Path, retry[3].Path);
        }
        finally
        {
            workspace.Delete(true);
        }
    }

    private static TdApi.File MakeFile(DirectoryInfo directory, int id, string content)
    {
        var source = Path.Combine(directory.FullName, "video.mp4");
        File.WriteAllText(source, content);
        return new()
        {
            Id = id,
            Size = new FileInfo(source).Length,
            Remote = new() { Id = $"synthetic-remote-{id}", UniqueId = $"synthetic-unique-{id}" },
            Local = new() { Path = source, IsDownloadingCompleted = true }
        };
    }
}
