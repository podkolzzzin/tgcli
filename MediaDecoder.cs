using System.Diagnostics;

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
    public static async Task<DecodedMedia> DecodeAsync(string input, string executable, string? temporaryRoot, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var directory = Directory.CreateDirectory(System.IO.Path.Combine(temporaryRoot ?? System.IO.Path.GetTempPath(), "tgcli-transcribe-" + Guid.NewGuid().ToString("N"))).FullName;
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var wav = System.IO.Path.Combine(directory, "audio.wav");
        try
        {
            var info = new ProcessStartInfo(executable)
            {
                RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true
            };
            foreach (var arg in new[] { "-nostdin", "-hide_banner", "-loglevel", "error", "-protocol_whitelist", "file,pipe", "-i", System.IO.Path.GetFullPath(input), "-map", "0:a:0", "-vn", "-ac", "1", "-ar", "16000", "-c:a", "pcm_s16le", "-y", wav })
                info.ArgumentList.Add(arg);
            var result = await MediaProcess.RunAsync(info, cancellationToken);
            if (result.ExitCode != 0 || !File.Exists(wav) || new FileInfo(wav).Length <= 44)
                throw new InvalidOperationException($"Unable to decode media audio (the file may have no audio track): {result.Error.Trim()}");
            return new(directory, wav);
        }
        catch
        {
            Directory.Delete(directory, recursive: true);
            throw;
        }
    }
}
