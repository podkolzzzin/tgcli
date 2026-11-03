using System.Diagnostics;
using Newtonsoft.Json.Linq;
using TdLib;

namespace TgCli;

internal sealed record TranscriptSegment(double Start, double End, string Text);

internal sealed record MediaTranscript(string Model, string Language, IReadOnlyList<TranscriptSegment> Segments)
{
    public string Text => string.Join(" ", Segments.Select(s => s.Text.Trim()).Where(s => s.Length > 0));

    public JObject ToJson() => new()
    {
        ["kind"] = "transcription",
        ["generated"] = true,
        ["model"] = Model,
        ["language"] = Language,
        ["text"] = Text,
        ["segments"] = new JArray(Segments.Select(s => new JObject
        {
            ["start"] = s.Start, ["end"] = s.End, ["text"] = s.Text.Trim()
        }))
    };
}

internal sealed record TranscriptionOptions(string Language, string? Command)
{
    // Whisper language codes; "auto" selects language from the media.
    private static readonly HashSet<string> Languages = new(
        ("auto en zh de es ru ko fr ja pt tr pl ca nl ar sv it id hi fi vi he uk el ms cs ro da hu ta no th ur hr bg lt la mi ml cy sk te fa lv bn sr az sl kn et mk br eu is hy ne mn bs kk sq sw gl mr pa si km sn yo so af oc ka be tg sd gu am yi lo uz fo ht ps tk nn mt sa lb my bo tl mg as tt haw ln ha ba jw su yue").Split(' '),
        StringComparer.Ordinal);

    public static TranscriptionOptions? Create(bool transcribe, string language, string? command)
    {
        if (transcribe && !string.IsNullOrWhiteSpace(command))
            throw new ArgumentException("Choose --transcribe or --transcribe-command, not both.");
        if (!transcribe && string.IsNullOrWhiteSpace(command)) return null;
        var normalized = language.Trim().ToLowerInvariant();
        if (!Languages.Contains(normalized))
            throw new ArgumentException($"Unsupported transcription language '{language}'. Use auto or a Whisper language code such as uk or en.");
        return new(normalized, string.IsNullOrWhiteSpace(command) ? null : command);
    }
}

internal interface IMediaTranscriber : IAsyncDisposable
{
    Task<MediaTranscript> TranscribeAsync(string path, string language, CancellationToken cancellationToken);
}

internal sealed class MediaTranscription(TranscriptionOptions options, Func<IMediaTranscriber> createEngine) : IAsyncDisposable
{
    private IMediaTranscriber? engine;

    public static bool Supports(string kind) => kind is "voice" or "video-note" or "audio" or "video";

    public static MessageFile GetMedia(TdApi.Message message)
    {
        var file = MessageFiles.GetPrimaryAttachment(message.Content);
        return file is not null && Supports(file.Kind)
            ? file
            : throw new ArgumentException("Message must contain a voice note, video note, audio or video attachment.");
    }

    public static string AppendToCaption(string caption, MediaTranscript transcript) =>
        string.IsNullOrWhiteSpace(caption)
            ? $"[Generated transcription]\n{transcript.Text}"
            : $"{caption}\n\n[Generated transcription]\n{transcript.Text}";

    public static async Task<MediaTranscript?> TranscribeAttachmentAsync(
        MessageFile file, MediaTranscription? transcription, Func<MessageFile, Task<string>> download,
        CancellationToken cancellationToken)
    {
        if (transcription is null || !Supports(file.Kind)) return null;
        cancellationToken.ThrowIfCancellationRequested();
        return await transcription.TranscribeFileAsync(await download(file), cancellationToken);
    }

    public static async Task<string> DownloadAsync(TelegramSession tg, MessageFile file, CancellationToken cancellationToken)
    {
        var downloaded = file.File.Local?.IsDownloadingCompleted == true && File.Exists(file.File.Local.Path)
            ? file.File
            : await tg.Client.DownloadFileAsync(file.FileId, 32, 0, 0, synchronous: true).WaitAsync(cancellationToken);
        var path = downloaded.Local?.Path;
        if (downloaded.Local?.IsDownloadingCompleted != true || string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            throw new InvalidOperationException($"Telegram did not provide a complete local media file for file_id={file.FileId}.");
        file.File.Local = downloaded.Local;
        return path;
    }

    public async Task<MediaTranscript> TranscribeFileAsync(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(path)) throw new FileNotFoundException("Media file does not exist.", path);
        if (options.Command is null)
            return await (engine ??= createEngine()).TranscribeAsync(Path.GetFullPath(path), options.Language, cancellationToken);

        var command = options.Command;
        var parts = File.Exists(command) ? [command] : command.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var info = new ProcessStartInfo(parts[0]) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        if (parts.Length == 2) info.ArgumentList.Add(parts[1]);
        info.ArgumentList.Add(Path.GetFullPath(path));
        var result = await MediaProcess.RunAsync(info, cancellationToken);
        if (result.ExitCode != 0) throw new InvalidOperationException($"Transcription command exited with code {result.ExitCode}: {result.Error.Trim()}");
        return new("external-command", options.Language, [new(0, 0, result.Output.Trim())]);
    }

    public async ValueTask DisposeAsync()
    {
        if (engine is not null) await engine.DisposeAsync();
    }
}

internal static class MediaProcess
{
    public static async Task<(int ExitCode, string Output, string Error)> RunAsync(ProcessStartInfo info, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var process = Process.Start(info) ?? throw new InvalidOperationException($"Unable to start '{info.FileName}'.");
        using var registration = cancellationToken.Register(() =>
        {
            try { process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
        });
        // Read both pipes concurrently: large decoder diagnostics must never block stdout or process exit.
        var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken);
            return (process.ExitCode, await output, await error);
        }
        catch
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            await process.WaitForExitAsync(CancellationToken.None);
            try { await Task.WhenAll(output, error); } catch (OperationCanceledException) { }
            throw;
        }
    }
}
