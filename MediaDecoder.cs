using System.Diagnostics;
using System.Globalization;

namespace TgCli;

internal sealed class DecodedMedia(string directory, string path) : IAsyncDisposable
{
    public string Path { get; } = path;
    public ValueTask DisposeAsync()
    {
        Directory.Delete(directory, recursive: true);
        return ValueTask.CompletedTask;
    }
}

internal static class MediaDecoder
{
    internal const int MaximumDurationMinutes = 30;
    private const int SampleRate = 16000;
    private const int MaximumPcmBytes = MaximumDurationMinutes * 60 * SampleRate * sizeof(short);
    private const uint Riff = 0x46464952;
    private const uint Wave = 0x45564157;
    private const uint DataChunk = 0x61746164;

    public static async Task<DecodedMedia> DecodeAsync(string input, string executable, string? temporaryRoot, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var directory = Directory.CreateDirectory(Path.Combine(temporaryRoot ?? Path.GetTempPath(), $"tgcli-transcribe-{Guid.NewGuid():N}")).FullName;
        var wav = Path.Combine(directory, "audio.wav");
        try
        {
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

            var info = new ProcessStartInfo(executable)
            {
                RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true
            };
            // Decode just beyond the supported duration so overlong media fails explicitly.
            var probeDuration = (MaximumDurationMinutes * 60 + 1).ToString(CultureInfo.InvariantCulture);
            string[] arguments =
            [
                "-nostdin", "-hide_banner", "-loglevel", "error", "-max_alloc", "134217728",
                "-threads", "2", "-filter_threads", "2", "-protocol_whitelist", "file,pipe",
                "-i", Path.GetFullPath(input), "-map", "0:a:0", "-vn", "-ac", "1",
                "-ar", SampleRate.ToString(CultureInfo.InvariantCulture), "-c:a", "pcm_s16le",
                "-t", probeDuration, "-y", wav
            ];
            foreach (var argument in arguments)
                info.ArgumentList.Add(argument);

            var result = await MediaProcess.RunAsync(info, cancellationToken);
            if (result.ExitCode != 0 || !File.Exists(wav))
                throw new InvalidOperationException($"Unable to decode media audio (the file may have no audio track): {result.Error.Trim()}");

            var pcmLength = ReadPcmLength(wav);
            if (pcmLength == 0)
                throw new InvalidOperationException("Unable to decode media audio: the audio track is empty.");
            if (pcmLength > MaximumPcmBytes)
                throw new InvalidOperationException($"Transcription supports a {MaximumDurationMinutes}-minute maximum audio duration.");
            cancellationToken.ThrowIfCancellationRequested();
            return new(directory, wav);
        }
        catch
        {
            Directory.Delete(directory, recursive: true);
            throw;
        }
    }

    private static uint ReadPcmLength(string path)
    {
        using var file = File.OpenRead(path);
        using var reader = new BinaryReader(file);
        if (file.Length >= 12 && reader.ReadUInt32() == Riff)
        {
            _ = reader.ReadUInt32(); // RIFF size, followed by its WAVE form type.
            if (reader.ReadUInt32() == Wave)
            {
                while (file.Length - file.Position >= 8)
                {
                    var chunk = reader.ReadUInt32();
                    var length = reader.ReadUInt32();
                    if (length > file.Length - file.Position)
                        break;
                    if (chunk == DataChunk)
                        return length;
                    file.Seek((long)length + (length & 1), SeekOrigin.Current);
                }
            }
        }

        throw new InvalidOperationException("Unable to decode media audio: the decoder produced an invalid WAV file.");
    }
}
