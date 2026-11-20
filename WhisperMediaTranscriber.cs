using Whisper.net;
using Whisper.net.LibraryLoader;

namespace TgCli;

internal sealed class WhisperMediaTranscriber : IMediaTranscriber
{
    private const int MaximumSegments = 32000;
    private const int MaximumTranscriptCharacters = 1024 * 1024;
    private static readonly object RuntimeGate = new();
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
            lock (RuntimeGate)
            {
                cancellationToken.ThrowIfCancellationRequested();
                RuntimeOptions.LibraryPath = Path.Combine(assets.DirectoryPath, "tgcli");
                RuntimeOptions.RuntimeLibraryOrder = [RuntimeLibrary.Cpu, RuntimeLibrary.CpuNoAvx];
                factory = WhisperFactory.FromBuffer(assets.ReadModel());
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        using var inference = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        await using var processor = factory.CreateBuilder()
            .WithLanguage(language)
            .WithThreads(Math.Clamp(Environment.ProcessorCount, 1, 4))
            .WithNoContext()
            .Build();
        await using var wav = File.OpenRead(audio.Path);
        var segments = new List<TranscriptSegment>();
        var detectedLanguage = language;
        var textLength = 0;
        try
        {
            await foreach (var segment in processor.ProcessAsync(wav, inference.Token))
            {
                var text = segment.Text.Trim();
                if (segments.Count == MaximumSegments || text.Length > MaximumTranscriptCharacters - textLength)
                {
                    inference.Cancel();
                    throw new InvalidOperationException("Generated transcription exceeded the supported output size.");
                }

                segments.Add(new(segment.Start.TotalSeconds, segment.End.TotalSeconds, text));
                textLength += text.Length;
                if (!string.IsNullOrWhiteSpace(segment.Language)) detectedLanguage = segment.Language;
            }
            cancellationToken.ThrowIfCancellationRequested();
            return new("whisper-base", detectedLanguage, segments);
        }
        finally
        {
            // The processor's DisposeAsync drains native inference before its factory or input is freed.
            inference.Cancel();
        }
    }

    public ValueTask DisposeAsync()
    {
        factory?.Dispose();
        factory = null;
        return ValueTask.CompletedTask;
    }
}
