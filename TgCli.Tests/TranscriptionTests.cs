using System;
using System.Linq;
using System.IO;
using System.Diagnostics;
using System.Text;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using TdLib;
using Xunit;

namespace TgCli.Tests;

public sealed class TranscriptionTests
{
    [Theory]
    [InlineData("voice", true)]
    [InlineData("video-note", true)]
    [InlineData("audio", true)]
    [InlineData("video", true)]
    [InlineData("photo", false)]
    [InlineData("document", false)]
    [InlineData("thumbnail", false)]
    public void SupportsSpeechMediaOnly(string kind, bool supported) =>
        Assert.Equal(supported, MediaTranscription.Supports(kind));

    [Fact]
    public void RejectsTwoEnginesAndInvalidLanguage()
    {
        Assert.Throws<ArgumentException>(() => TranscriptionOptions.Create(true, "uk", "some-command"));
        Assert.Throws<ArgumentException>(() => TranscriptionOptions.Create(true, "not-a-language", null));
        Assert.Equal("uk", TranscriptionOptions.Create(true, "UK", null)!.Language);
        Assert.Null(TranscriptionOptions.Create(false, "auto", null));
    }

    [Fact]
    public void SelectsPrimaryVideoInsteadOfThumbnail()
    {
        var message = new TdApi.Message
        {
            Content = new TdApi.MessageContent.MessageVideo
            {
                Video = new TdApi.Video
                {
                    Video_ = new TdApi.File { Id = 10 },
                    Thumbnail = new TdApi.Thumbnail { File = new TdApi.File { Id = 11 } }
                }
            }
        };
        Assert.Equal(10, MediaTranscription.GetMedia(message).FileId);
        Assert.Throws<ArgumentException>(() => MediaTranscription.GetMedia(new TdApi.Message
        {
            Content = new TdApi.MessageContent.MessageText { Text = new TdApi.FormattedText { Text = "hello" } }
        }));
    }

    [Fact]
    public void StructuredTranscriptPreservesSegmentsAndGeneratedLabel()
    {
        var transcript = new MediaTranscript("whisper-base", "uk", [new TranscriptSegment(1.25, 2.5, " Привіт! ")]);
        var json = transcript.ToJson();
        Assert.Equal("Привіт!", transcript.Text);
        Assert.True(json.Value<bool>("generated"));
        Assert.Equal("transcription", json.Value<string>("kind"));
        Assert.Equal("whisper-base", json.Value<string>("model"));
        Assert.Equal("uk", json.Value<string>("language"));
        Assert.Equal(1.25, json["segments"]![0]!.Value<double>("start"));
        Assert.Equal(2.5, json["segments"]![0]!.Value<double>("end"));
        Assert.Equal("caption\n\n[Generated transcription]\nПривіт!", MediaTranscription.AppendToCaption("caption", transcript));
    }

    [Fact]
    public async Task DisabledAndNonPrimaryFilesDoNotDownload()
    {
        var file = new MessageFile("thumbnail", 7, null, null, null, null, null, null, null, null, "thumbnail", new TdApi.File());
        Assert.Null(await MediaTranscription.TranscribeAttachmentAsync(file, null, _ => throw new Exception("download"), CancellationToken.None));
    }
    [Fact]
    public async Task LocalCommandValidatesBeforeLoadingTheModel()
    {
        var info = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var arg in new[] { typeof(Program).Assembly.Location, "transcribe", "--input", "/missing/tgcli-test-audio.mp3" }) info.ArgumentList.Add(arg);
        var result = await MediaProcess.RunAsync(info, TestContext.Current.CancellationToken);
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("Media file does not exist", result.Error);
        Assert.Empty(result.Output);
    }

    [Fact]
    public async Task DecoderReportsFailureWithoutLeakingTemporaryFiles()
    {
        var input = Path.GetTempFileName();
        var parent = Path.Combine(Path.GetTempPath(), "tgcli-decoder-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(parent);
        try
        {
            await File.WriteAllTextAsync(input, "not audio", TestContext.Current.CancellationToken);
            // A real failing process exercises diagnostics and cleanup without a model or FFmpeg dependency.
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                MediaDecoder.DecodeAsync(input, "dotnet", parent, TestContext.Current.CancellationToken));
            Assert.Contains("decode", error.Message.ToLowerInvariant());
            Assert.Empty(Directory.EnumerateFileSystemEntries(parent));
        }
        finally { File.Delete(input); Directory.Delete(parent, true); }
    }

    [Fact]
    public void AttachmentTranscriptDoesNotReplaceTheOriginalCaption()
    {
        var file = new MessageFile("audio", 7, null, null, "lecture.mp3", "audio/mpeg", null, null, 30, 100, "audio", new TdApi.File());
        var transcript = new MediaTranscript("whisper-base", "en", [new(0, 3, "spoken words")]);
        var attachment = ExportSchema.ToAttachmentJson(file, "original caption", transcript);
        Assert.Equal("original caption", attachment.Value<string>("caption"));
        Assert.Equal("spoken words", attachment["generated_text"]!.Value<string>("text"));
        Assert.True(attachment["generated_text"]!.Value<bool>("generated"));
        Assert.Equal("audio", attachment.Value<string>("kind"));
    }

    [Fact]
    public void EmbeddedAssetExtractionRepairsCorruptCacheAndRejectsBadSource()
    {
        var directory = Path.Combine(Path.GetTempPath(), "tgcli-assets-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var target = Path.Combine(directory, "asset");
        var bytes = Encoding.UTF8.GetBytes("verified asset");
        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        try
        {
            File.WriteAllText(target, "corrupt cache");
            using (var source = new MemoryStream(bytes)) EmbeddedTranscriptionAssets.ExtractVerified(source, target, hash);
            Assert.Equal("verified asset", File.ReadAllText(target));
            File.WriteAllText(target, "keep previous file on failure");
            using var corrupt = new MemoryStream(Encoding.UTF8.GetBytes("wrong bytes"));
            Assert.Throws<InvalidOperationException>(() => EmbeddedTranscriptionAssets.ExtractVerified(corrupt, target, hash));
            Assert.Equal("keep previous file on failure", File.ReadAllText(target));
            Assert.Single(Directory.EnumerateFiles(directory));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task MissingLocalMediaAndCancellationNeverLoadTheEngine()
    {
        await using var transcription = new MediaTranscription(new("en", null), () => throw new Exception("engine must not load"));
        await Assert.ThrowsAsync<FileNotFoundException>(() => transcription.TranscribeFileAsync("/does-not-exist-tgcli.wav", TestContext.Current.CancellationToken));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => transcription.TranscribeFileAsync("/does-not-exist-tgcli.wav", cancellation.Token));
    }

    private static string Decoder()
    {
        var name = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";
        var prepared = Path.Combine(Directory.GetCurrentDirectory(), ".transcription", System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier, name);
        if (File.Exists(prepared)) return prepared;
        if (File.Exists("/usr/bin/ffmpeg")) return "/usr/bin/ffmpeg";
        Assert.Skip("Prepare transcription assets to exercise the real decoder.");
        return "";
    }

    [Fact]
    public async Task DecoderNormalizesStereoMediaTo16KhzMonoAndCleansUp()
    {
        var decoder = Decoder();
        var directory = Path.Combine(Path.GetTempPath(), "tgcli-audio-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var input = Path.Combine(directory, "stereo recording.wav");
        var temporary = Path.Combine(directory, "decoded");
        Directory.CreateDirectory(temporary);
        try
        {
            using (var writer = new BinaryWriter(File.Create(input)))
            {
                var sampleCount = 4410;
                writer.Write(Encoding.ASCII.GetBytes("RIFF")); writer.Write(36 + sampleCount * 4);
                writer.Write(Encoding.ASCII.GetBytes("WAVEfmt ")); writer.Write(16);
                writer.Write((short)1); writer.Write((short)2); writer.Write(44100); writer.Write(176400);
                writer.Write((short)4); writer.Write((short)16);
                writer.Write(Encoding.ASCII.GetBytes("data")); writer.Write(sampleCount * 4);
                for (var n = 0; n < sampleCount; n++) { writer.Write((short)100); writer.Write((short)-100); }
            }
            string decodedPath;
            await using (var audio = await MediaDecoder.DecodeAsync(input, decoder, temporary, TestContext.Current.CancellationToken))
            {
                decodedPath = audio.Path;
                var header = await File.ReadAllBytesAsync(audio.Path, TestContext.Current.CancellationToken);
                Assert.Equal("RIFF", Encoding.ASCII.GetString(header, 0, 4));
                Assert.Equal((short)1, BitConverter.ToInt16(header, 22));
                Assert.Equal(16000, BitConverter.ToInt32(header, 24));
                Assert.Equal((short)16, BitConverter.ToInt16(header, 34));
            }
            Assert.False(File.Exists(decodedPath));
            Assert.Empty(Directory.EnumerateFileSystemEntries(temporary));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task CancelingDecoderProcessTerminatesItPromptly()
    {
        var info = new ProcessStartInfo(Decoder()) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var arg in new[] { "-nostdin", "-v", "error", "-f", "lavfi", "-i", "anullsrc", "-f", "null", "-" }) info.ArgumentList.Add(arg);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellation.CancelAfter(TimeSpan.FromMilliseconds(250));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => MediaProcess.RunAsync(info, cancellation.Token).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
    }

}
