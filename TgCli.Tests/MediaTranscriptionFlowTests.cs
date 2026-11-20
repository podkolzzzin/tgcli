#nullable enable

using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using TdLib;
using Xunit;

namespace TgCli.Tests;

public sealed class MediaTranscriptionFlowTests
{
    [Fact]
    public async Task EnabledEngineIgnoresNonPrimaryVideo()
    {
        var file = new MessageFile("video", 7, null, null, null, null, null, null, null, null, "preview", new TdApi.File(), "auxiliary");
        await using var transcription = new MediaTranscription(new("en", null), () => throw new Exception("engine started"));
        Assert.Null(await MediaTranscription.TranscribeAttachmentAsync(file, transcription,
            _ => throw new Exception("download started"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ExternalExecutableStringIsNotParsedAsArguments()
    {
        if (OperatingSystem.IsWindows())
            Assert.Skip("Uses the Unix echo executable.");

        var input = Path.GetTempFileName();
        try
        {
            await using var transcription = new MediaTranscription(new("en", "/bin/echo extra"), () => throw new Exception("engine started"));
            await Assert.ThrowsAsync<System.ComponentModel.Win32Exception>(() =>
                transcription.TranscribeFileAsync(input, TestContext.Current.CancellationToken));
        }
        finally
        {
            File.Delete(input);
        }
    }

    [Fact]
    public async Task ExternalStderrIsDrainedWithBoundedRetention()
    {
        if (OperatingSystem.IsWindows())
            Assert.Skip("Uses shell tools for synthetic output.");

        var info = new ProcessStartInfo("/bin/sh") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        info.ArgumentList.Add("-c");
        info.ArgumentList.Add("head -c 2000000 /dev/zero >&2; printf 'transcript'");
        var result = await MediaProcess.RunAsync(info, TestContext.Current.CancellationToken);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("transcript", result.Output);
        Assert.Contains("truncated", result.Error);
        Assert.True(result.Error.Length < 1500000);
    }

    [Fact]
    public async Task ExternalStdoutOverflowFailsInsteadOfReturningAClippedTranscript()
    {
        if (OperatingSystem.IsWindows())
            Assert.Skip("Uses shell tools for synthetic output.");

        var info = new ProcessStartInfo("/bin/sh") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        info.ArgumentList.Add("-c");
        info.ArgumentList.Add("head -c 2000000 /dev/zero");
        await Assert.ThrowsAsync<InvalidOperationException>(() => MediaProcess.RunAsync(info, TestContext.Current.CancellationToken));
    }
}
