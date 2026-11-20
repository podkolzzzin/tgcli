#nullable enable

using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
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
            var error = await Record.ExceptionAsync(() =>
                transcription.TranscribeFileAsync(input, TestContext.Current.CancellationToken));
            Assert.True(error is System.ComponentModel.Win32Exception or InvalidOperationException, error?.Message);
        }
        finally
        {
            File.Delete(input);
        }
    }

    [Fact]
    public async Task ExternalExecutableWithoutShebangDoesNotRunThroughAShell()
    {
        if (!OperatingSystem.IsLinux())
            Assert.Skip("Exercises Linux executable format handling.");

        var directory = Directory.CreateTempSubdirectory("tgcli-media-noshell-");
        try
        {
            var executable = Path.Combine(directory.FullName, "plain-executable");
            var input = Path.Combine(directory.FullName, "fixture.wav");
            File.WriteAllText(input, "fixture");
            File.WriteAllText(executable, "printf owned > \"$1.marker\"");
            if (OperatingSystem.IsLinux())
                File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            await using var transcription = TranscriptionCommands.Create(false, "en", executable)!;
            var error = await Record.ExceptionAsync(() =>
                transcription.TranscribeFileAsync(input, TestContext.Current.CancellationToken));

            Assert.False(File.Exists(input + ".marker"), "An implicit shell ran a file with no executable format.");
            Assert.NotNull(error);
        }
        finally
        {
            directory.Delete(true);
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

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public async Task LauncherExitWithInheritedPipesHasBoundedOutcome(bool supervised, bool cancel)
    {
        if (!OperatingSystem.IsLinux())
            Assert.Skip("Requires Linux process groups and /proc.");

        var directory = Directory.CreateTempSubdirectory("tgcli-media-process-");
        var pidFile = Path.Combine(directory.FullName, "child.pid");
        var info = new ProcessStartInfo("/bin/sh") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in new[] { "-c", "sleep 30 & echo \"$$ $!\" > \"$1\"; exit 0", "fixture", pidFile })
            info.ArgumentList.Add(argument);
        var testToken = TestContext.Current.CancellationToken;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(testToken);
        var operation = MediaProcess.RunAsync(info, cancellation.Token, superviseExternal: supervised);
        int childPid = 0;
        try
        {
            using var readiness = CancellationTokenSource.CreateLinkedTokenSource(testToken);
            readiness.CancelAfter(TimeSpan.FromSeconds(5));
            string[] ids = [];
            while (!File.Exists(pidFile) || (ids = (await File.ReadAllTextAsync(pidFile, readiness.Token))
                .Trim().Split(' ')).Length != 2)
            {
                await Task.Delay(20, readiness.Token);
            }

            childPid = int.Parse(ids[1]);
            while (GetStatus(childPid, "PPid:") == ids[0])
                await Task.Delay(20, readiness.Token);
            if (supervised)
            {
                var stat = File.ReadAllText($"/proc/{childPid}/stat");
                var fields = stat[(stat.LastIndexOf(')') + 2)..].Split(' ');
                var groupId = int.Parse(fields[2]);
                Assert.NotEqual(Environment.ProcessId, groupId);
                while (IsRunning(groupId))
                    await Task.Delay(20, readiness.Token);
            }

            Assert.True(IsRunning(childPid));
            if (cancel)
            {
                cancellation.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation.WaitAsync(TimeSpan.FromSeconds(5), testToken));
            }
            else
            {
                var error = await Assert.ThrowsAsync<InvalidOperationException>(() => operation.WaitAsync(TimeSpan.FromSeconds(5), testToken));
                Assert.Contains(supervised ? "descendant held its output pipes open" : "Descendant cleanup could not be verified", error.Message);
            }
            if (!supervised)
                return;

            var stopped = Stopwatch.StartNew();
            while (IsRunning(childPid) && stopped.Elapsed < TimeSpan.FromSeconds(1))
                await Task.Delay(20, testToken);
            Assert.False(IsRunning(childPid), "The inherited-pipe child kept running after cancellation.");
        }
        finally
        {
            cancellation.Cancel();
            if (childPid > 0 && IsRunning(childPid))
            {
                using var child = Process.GetProcessById(childPid);
                child.Kill();
            }
            directory.Delete(true);
        }
    }

    private static bool IsRunning(int processId) =>
        GetStatus(processId, "State:") is { } state && !state.StartsWith("Z", StringComparison.Ordinal);

    private static string? GetStatus(int processId, string field)
    {
        try
        {
            return File.ReadLines($"/proc/{processId}/status")
                .FirstOrDefault(line => line.StartsWith(field, StringComparison.Ordinal))?[field.Length..].Trim();
        }
        catch (IOException)
        {
            return null;
        }
    }

    [Fact]
    public async Task DecodingOverlongAudioFailsAndCleansUp()
    {
        var decoder = "/usr/bin/ffmpeg";
        if (!File.Exists(decoder))
            Assert.Skip("Requires a local FFmpeg executable.");

        var directory = Directory.CreateTempSubdirectory("tgcli-long-media-");
        try
        {
            var input = Path.Combine(directory.FullName, "long.wav");
            const int sampleBytes = (30 * 60 + 2) * 16000 * 2;
            using (var writer = new BinaryWriter(File.Create(input)))
            {
                writer.Write("RIFF"u8);
                writer.Write(36 + sampleBytes);
                writer.Write("WAVEfmt "u8);
                writer.Write(16);
                writer.Write((short)1);
                writer.Write((short)1);
                writer.Write(16000);
                writer.Write(32000);
                writer.Write((short)2);
                writer.Write((short)16);
                writer.Write("data"u8);
                writer.Write(sampleBytes);
                writer.BaseStream.SetLength(44 + sampleBytes);
            }

            var error = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            {
                await using var _ = await MediaDecoder.DecodeAsync(input, decoder, directory.FullName, TestContext.Current.CancellationToken);
            });
            Assert.Contains("30-minute", error.Message);
            Assert.Empty(directory.GetDirectories());
        }
        finally
        {
            directory.Delete(true);
        }
    }

    [Fact]
    public async Task ExplicitExecutableArgumentsArePassedUnmodified()
    {
        if (OperatingSystem.IsWindows())
            Assert.Skip("Uses a Unix executable path with spaces.");

        var directory = Directory.CreateTempSubdirectory("tgcli-media-args-");
        try
        {
            var executable = Path.Combine(directory.FullName, "transcribe fixture");
            var input = Path.Combine(directory.FullName, "media with spaces.wav");
            File.WriteAllText(executable, "#!/bin/sh\nprintf '<%s>\\n' \"$@\"");
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            File.WriteAllText(input, "fixture");

            await using var transcription = TranscriptionCommands.Create(false, "en", executable, ["one two", "$HOME"])!;
            var transcript = await transcription.TranscribeFileAsync(input, TestContext.Current.CancellationToken);
            Assert.Equal($"<one two>\n<$HOME>\n<{input}>", transcript.Text);
        }
        finally
        {
            directory.Delete(true);
        }
    }

    [Fact]
    public async Task ExportTranscribesOnlyPrimaryMediaAndKeepsTheCaption()
    {
        var video = new TdApi.MessageContent.MessageVideo
        {
            Caption = new TdApi.FormattedText { Text = "Original caption" },
            Video = new TdApi.Video
            {
                Video_ = new TdApi.File { Id = 10 },
                Thumbnail = new TdApi.Thumbnail { File = new TdApi.File { Id = 11 } }
            }
        };
        var input = Path.GetTempFileName();
        try
        {
            var message = new TdApi.Message { Content = video };
            await using var transcription = new MediaTranscription(new("uk", null), () => new FixtureTranscriber());
            var rows = await ExportSchema.BuildAttachmentRowsAsync(message, MessageFiles.GetAllFiles(message), transcription, file =>
            {
                Assert.Equal(10, file.FileId);
                return Task.FromResult(input);
            }, TestContext.Current.CancellationToken);

            Assert.Equal(2, rows.Count);
            Assert.Equal("Original caption", (string?)rows[0]["caption"]);
            Assert.True((bool?)rows[0]["generated_text"]!["generated"]);
            Assert.Equal("uk", (string?)rows[0]["generated_text"]!["language"]);
            Assert.Equal("thumbnail", (string?)rows[1]["role"]);
            Assert.Equal(Newtonsoft.Json.Linq.JTokenType.Null, rows[1]["generated_text"]!.Type);
        }
        finally
        {
            File.Delete(input);
        }
    }

    [Fact]
    public async Task ConcurrentAssetExtractionKeepsOnlyVerifiedBytes()
    {
        var directory = Directory.CreateTempSubdirectory("tgcli-media-extract-");
        try
        {
            var target = Path.Combine(directory.FullName, "asset");
            var content = Encoding.UTF8.GetBytes("verified synthetic data");
            var checksum = Convert.ToHexString(SHA256.HashData(content));
            File.WriteAllText(target, "corrupt");
            await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => Task.Run(() =>
            {
                using var source = new MemoryStream(content);
                EmbeddedTranscriptionAssets.ExtractVerified(source, target, checksum);
            }, TestContext.Current.CancellationToken)));

            Assert.Equal(content, File.ReadAllBytes(target));
            Assert.Single(directory.GetFiles());
        }
        finally
        {
            directory.Delete(true);
        }
    }

    private sealed class FixtureTranscriber : IMediaTranscriber
    {
        public Task<MediaTranscript> TranscribeAsync(string path, string language, CancellationToken cancellationToken) =>
            Task.FromResult(new MediaTranscript("fixture", language, [new(0, 2, "Synthetic speech")]));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
