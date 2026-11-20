#nullable enable

using System;
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

    private static TdApi.File MakeFile(DirectoryInfo directory, int id, string content)
    {
        var source = Path.Combine(directory.FullName, "video.mp4");
        File.WriteAllText(source, content);
        return new()
        {
            Id = id,
            Size = new FileInfo(source).Length,
            Remote = new() { Id = "synthetic-remote", UniqueId = "synthetic-unique" },
            Local = new() { Path = source, IsDownloadingCompleted = true }
        };
    }
}
