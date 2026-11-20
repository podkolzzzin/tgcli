using System.Diagnostics;
using System.Text;
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

internal sealed record TranscriptionOptions(string Language, string? Command, IReadOnlyList<string>? CommandArguments = null)
{
    // Whisper language codes; "auto" selects language from the media.
    private static readonly HashSet<string> Languages = new(
        ("auto en zh de es ru ko fr ja pt tr pl ca nl ar sv it id hi fi vi he uk el ms cs ro da hu ta no th ur hr bg lt la mi ml cy sk te fa lv bn sr az sl kn et mk br eu is hy ne mn bs kk sq sw gl mr pa si km sn yo so af oc ka be tg sd gu am yi lo uz fo ht ps tk nn mt sa lb my bo tl mg as tt haw ln ha ba jw su yue").Split(' '),
        StringComparer.Ordinal);

    public static TranscriptionOptions? Create(bool transcribe, string language, string? command, IReadOnlyList<string>? commandArguments = null)
    {
        if (transcribe && !string.IsNullOrWhiteSpace(command))
            throw new ArgumentException("Choose --transcribe or --transcribe-command, not both.");
        if (commandArguments is { Count: > 0 } && string.IsNullOrWhiteSpace(command))
            throw new ArgumentException("External arguments require --transcribe-command.");
        if (!transcribe && string.IsNullOrWhiteSpace(command)) return null;
        var normalized = language.Trim().ToLowerInvariant();
        if (!Languages.Contains(normalized))
            throw new ArgumentException($"Unsupported transcription language '{language}'. Use auto or a Whisper language code such as uk or en.");
        return new(normalized, string.IsNullOrWhiteSpace(command) ? null : command, commandArguments?.ToArray());
    }
}

internal interface IMediaTranscriber : IAsyncDisposable
{
    Task<MediaTranscript> TranscribeAsync(string path, string language, CancellationToken cancellationToken);
}

internal sealed class MediaTranscription(TranscriptionOptions options, Func<IMediaTranscriber> createEngine) : IAsyncDisposable
{
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromMinutes(15);
    private readonly SemaphoreSlim gate = new(1);
    private IMediaTranscriber? engine;
    private bool disposed;

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
        if (transcription is null || file.Role != "primary" || !Supports(file.Kind)) return null;
        cancellationToken.ThrowIfCancellationRequested();
        return await transcription.TranscribeFileAsync(await download(file), cancellationToken);
    }

    public static async Task<string> DownloadAsync(
        TelegramSession tg, MessageFile file, CancellationToken cancellationToken, bool local = false,
        long chatId = 0, long messageId = 0)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (AtomicDownload.TryGetCompletedPath(file.File) is { } completedPath)
            return completedPath;
        if (local)
            throw new InvalidOperationException($"Media file_id={file.FileId} is unavailable in the local cache.");

        var downloaded = await TgCommands.DownloadFileWithRefreshAsync(
            tg, file.File, file.Kind, chatId, messageId, cancellationToken);
        file.File.Local = downloaded.Local;
        return AtomicDownload.GetCompletedPath(downloaded);
    }

    public async Task<MediaTranscript> TranscribeFileAsync(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(path)) throw new FileNotFoundException("Media file does not exist.", path);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(OperationTimeout);
        try
        {
            await gate.WaitAsync(deadline.Token);
            try
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                var absolutePath = Path.GetFullPath(path);
                return options.Command is null
                    ? await (engine ??= createEngine()).TranscribeAsync(absolutePath, options.Language, deadline.Token)
                    : await RunExternalAsync(absolutePath, deadline.Token);
            }
            finally
            {
                gate.Release();
            }
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"Transcription exceeded the {OperationTimeout.TotalMinutes:0}-minute time limit.");
        }
    }

    private async Task<MediaTranscript> RunExternalAsync(string absolutePath, CancellationToken cancellationToken)
    {
        var info = new ProcessStartInfo(options.Command!)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var arg in options.CommandArguments ?? [])
            info.ArgumentList.Add(arg);
        info.ArgumentList.Add(absolutePath);
        var result = await MediaProcess.RunAsync(info, cancellationToken);
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"Transcription command exited with code {result.ExitCode}: {result.Error.Trim()}");
        return new("external-command", options.Language, [new(0, 0, result.Output.Trim())]);
    }

    public async ValueTask DisposeAsync()
    {
        await gate.WaitAsync();
        try
        {
            disposed = true;
            if (engine is not null)
            {
                await engine.DisposeAsync();
                engine = null;
            }
        }
        finally
        {
            gate.Release();
        }
    }
}

internal static class MediaProcess
{
    private const int MaximumOutputCharacters = 1024 * 1024;
    private const int MaximumErrorCharacters = 64 * 1024;

    public static async Task<(int ExitCode, string Output, string Error)> RunAsync(ProcessStartInfo info, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var process = Process.Start(info) ?? throw new InvalidOperationException($"Unable to start '{info.FileName}'.");
        using var registration = cancellationToken.Register(() => Stop(process));

        var output = DrainAsync(process.StandardOutput, MaximumOutputCharacters, cancellationToken);
        var error = DrainAsync(process.StandardError, MaximumErrorCharacters, cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken);
            await Task.WhenAll(output, error);
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch
        {
            Stop(process);
            await process.WaitForExitAsync(CancellationToken.None);
            try { await Task.WhenAll(output, error); } catch (Exception ex) when (ex is OperationCanceledException or IOException) { }
            throw;
        }

        var standardOutput = await output;
        if (standardOutput.Truncated)
            throw new InvalidOperationException($"Process stdout exceeded the {MaximumOutputCharacters}-character limit.");
        var standardError = await error;
        return (process.ExitCode, standardOutput.Text,
            standardError.Truncated ? standardError.Text + "\n[stderr truncated]" : standardError.Text);
    }

    private static async Task<(string Text, bool Truncated)> DrainAsync(StreamReader reader, int limit, CancellationToken cancellationToken)
    {
        var builder = new StringBuilder();
        var buffer = new char[4096];
        var truncated = false;
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken)) != 0)
        {
            var retained = Math.Min(read, limit - builder.Length);
            builder.Append(buffer, 0, retained);
            truncated |= retained < read;
        }

        return (builder.ToString(), truncated);
    }

    private static void Stop(Process process)
    {
        try { process.Kill(entireProcessTree: true); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }
}
