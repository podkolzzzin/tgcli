using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace TgCli.Tests;

public sealed class EmbeddedTranscriptionAssetTests
{
    [Fact]
    public async Task WaitingExtractionReusesTheVerifiedRepair()
    {
        var directory = Directory.CreateTempSubdirectory("tgcli-asset-repair-");
        var target = Path.Combine(directory.FullName, "asset");
        var competingTarget = Path.Combine(directory.FullName, ".", "asset");
        if (OperatingSystem.IsWindows()) competingTarget = competingTarget.ToUpperInvariant();
        var content = "verified asset"u8.ToArray();
        var checksum = Convert.ToHexString(SHA256.HashData(content));
        var timeout = TimeSpan.FromSeconds(10);
        var token = TestContext.Current.CancellationToken;
        using var copyStarted = new ManualResetEventSlim();
        using var finishCopy = new ManualResetEventSlim();
        using var source = new PausedSource(content, copyStarted, finishCopy, token);
        using var unusedSource = new MemoryStream("invalid fallback"u8.ToArray());
        var contenderDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var contender = new Thread(() =>
        {
            try
            {
                EmbeddedTranscriptionAssets.ExtractVerified(unusedSource, competingTarget, checksum);
                contenderDone.SetResult();
            }
            catch (Exception ex) { contenderDone.SetException(ex); }
        }) { IsBackground = true };

        try
        {
            File.WriteAllText(target, "corrupt");
            var repair = Task.Run(() => EmbeddedTranscriptionAssets.ExtractVerified(source, target, checksum), token);
            Task competingExtraction = Task.CompletedTask;
            try
            {
                Assert.True(copyStarted.Wait(timeout, token));
                contender.Start();
                competingExtraction = contenderDone.Task;
                // Hold the repair until the contender is waiting or has attempted its own repair.
                Assert.True(SpinWait.SpinUntil(() => competingExtraction.IsCompleted ||
                    (contender.ThreadState & ThreadState.WaitSleepJoin) != 0, timeout));
            }
            finally
            {
                finishCopy.Set();
                await Task.WhenAll(repair, competingExtraction).WaitAsync(timeout, CancellationToken.None);
            }

            Assert.Equal(0, unusedSource.Position);
            Assert.Equal(content, File.ReadAllBytes(target));
        }
        finally { directory.Delete(true); }
    }

    private sealed class PausedSource(byte[] content, ManualResetEventSlim copyStarted, ManualResetEventSlim finishCopy,
        CancellationToken token) : MemoryStream(content)
    {
        public override void CopyTo(Stream destination, int bufferSize)
        {
            copyStarted.Set();
            finishCopy.Wait(token);
            base.CopyTo(destination, bufferSize);
        }
    }
}
