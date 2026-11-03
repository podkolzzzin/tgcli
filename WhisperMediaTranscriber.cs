using Whisper.net;
using Whisper.net.LibraryLoader;

namespace TgCli;

internal sealed class WhisperMediaTranscriber : IMediaTranscriber
{
    private WhisperFactory? factory;

    public async Task<MediaTranscript> TranscribeAsync(string path, string language, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var assets = EmbeddedTranscriptionAssets.Default;
        await using var audio = await MediaDecoder.DecodeAsync(path, assets.DecoderPath, null, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (factory is null)
        {
            // Native loading needs the runtime directory tree even in a single-file .NET publish.
            RuntimeOptions.LibraryPath = Path.Combine(assets.DirectoryPath, "tgcli");
            RuntimeOptions.RuntimeLibraryOrder = [RuntimeLibrary.Cpu, RuntimeLibrary.CpuNoAvx];
            factory = WhisperFactory.FromBuffer(assets.ReadModel());
        }
        await using var processor = factory.CreateBuilder()
            .WithLanguage(language)
            .WithThreads(Math.Clamp(Environment.ProcessorCount, 1, 8))
            .WithNoContext()
            .Build();
        await using var wav = File.OpenRead(audio.Path);
        var segments = new List<TranscriptSegment>();
        var detectedLanguage = language;
        await foreach (var segment in processor.ProcessAsync(wav, cancellationToken))
        {
            segments.Add(new(segment.Start.TotalSeconds, segment.End.TotalSeconds, segment.Text.Trim()));
            if (!string.IsNullOrWhiteSpace(segment.Language)) detectedLanguage = segment.Language;
        }
        cancellationToken.ThrowIfCancellationRequested();
        return new("whisper-base", detectedLanguage, segments);
    }

    public ValueTask DisposeAsync()
    {
        factory?.Dispose();
        factory = null;
        return ValueTask.CompletedTask;
    }
}
