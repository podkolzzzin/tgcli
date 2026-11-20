using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
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
        var result = await MediaProcess.RunAsync(info, cancellationToken, superviseExternal: true);
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

    private const string ChildArgument = "--tgcli-internal-media-child";
    private const string GroupReady = "tgcli-media-group-ready";
    private const string StartChild = "start";
    private const string StartRegistration = "start-registration";
    private const int SigKill = 9;
    private const int PrSetChildSubreaper = 36;
    private const int Interrupted = 4; // EINTR
    private const int NoChildProcesses = 10; // ECHILD
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan PipeExitTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(2);

    public static Task<(int ExitCode, string Output, string Error)> RunAsync(
        ProcessStartInfo info, CancellationToken cancellationToken, bool superviseExternal = false) =>
        RunAsync(info, cancellationToken, superviseExternal, registration: false);

    internal static async Task<int> RunRegistrationAsync(ProcessStartInfo info, CancellationToken cancellationToken) =>
        (await RunAsync(info, cancellationToken, superviseExternal: true, registration: true)).ExitCode;

    private static async Task<(int ExitCode, string Output, string Error)> RunAsync(
        ProcessStartInfo info, CancellationToken cancellationToken, bool superviseExternal, bool registration)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var supervised = superviseExternal && OperatingSystem.IsLinux();
        var launch = supervised ? ChildStartInfo(info) : info;
        using var process = Process.Start(launch) ?? throw new InvalidOperationException($"Unable to start '{info.FileName}'.");
        using var lifetime = supervised && registration ? process.StandardInput : null;
        using var outputReader = process.StandardOutput;
        using var errorReader = process.StandardError;
        var ownedGroup = 0;
        var stopped = 0;
        void StopOnce()
        {
            if (Interlocked.Exchange(ref stopped, 1) != 0)
                return;
            if (lifetime is not null)
                lifetime.Close();
            else
                Stop(process, Volatile.Read(ref ownedGroup));
        }

        using var cancellation = lifetime is null ? cancellationToken.Register(StopOnce) : default;
        using var pipeCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var error = DrainAsync(errorReader, registration ? 0 : MaximumErrorCharacters, pipeCancellation.Token);
        Task drains = error;
        Task<(string Text, bool Truncated)> output;
        try
        {
            if (supervised)
            {
                // The child confirms the new group and waits for our acknowledgment before running external code.
                var ready = await outputReader.ReadLineAsync(pipeCancellation.Token).AsTask()
                    .WaitAsync(StartupTimeout, cancellationToken);
                if (ready != GroupReady || GetProcessGroup(process.Id) != process.Id)
                    throw new InvalidOperationException("The supervisor could not establish an owned Linux process group.");

                Volatile.Write(ref ownedGroup, process.Id);
                cancellationToken.ThrowIfCancellationRequested();
                process.StandardInput.WriteLine(lifetime is null ? StartChild : StartRegistration);
            }
            if (lifetime is null && (supervised || registration))
                process.StandardInput.Close();

            output = DrainAsync(outputReader, registration ? 0 : MaximumOutputCharacters, pipeCancellation.Token);
            drains = Task.WhenAll(output, error);
            await process.WaitForExitAsync(cancellationToken);
            try
            {
                await drains.WaitAsync(PipeExitTimeout, cancellationToken);
            }
            catch (TimeoutException ex)
            {
                if (registration)
                    throw new IOException("The client launcher exited while a descendant held its output pipes open. Registration may have changed the configuration. Use a client launcher that waits for its child processes.", ex);
                var detail = supervised
                    ? "The media executable exited while a descendant held its output pipes open."
                    : "The media executable exited with an unowned descendant holding its output pipes open. Descendant cleanup could not be verified.";
                throw new InvalidOperationException($"{detail} The executable must wait for its child processes.", ex);
            }

            cancellationToken.ThrowIfCancellationRequested();
        }
        catch
        {
            StopOnce();
            pipeCancellation.Cancel();
            var shutdown = Task.WhenAll(process.WaitForExitAsync(), drains);
            try
            {
                await shutdown.WaitAsync(ShutdownTimeout);
            }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException or IOException)
            {
                _ = shutdown.ContinueWith(task => _ = task.Exception, CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
            throw;
        }

        var standardOutput = await output;
        if (standardOutput.Truncated)
            throw new InvalidOperationException($"Process stdout exceeded the {MaximumOutputCharacters}-character limit.");
        var standardError = await error;
        return (process.ExitCode, standardOutput.Text,
            standardError.Truncated ? standardError.Text + "\n[stderr truncated]" : standardError.Text);
    }

    [UnconditionalSuppressMessage("SingleFile", "IL3000", Justification = "Bundled assemblies have no location, so this intentionally falls back to the current executable.")]
    private static ProcessStartInfo ChildStartInfo(ProcessStartInfo info)
    {
        if (!string.IsNullOrEmpty(info.Arguments))
            throw new ArgumentException("Media executable arguments must be supplied as individual elements.");

        var processPath = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot locate the media supervisor host.");
        var assembly = typeof(MediaProcess).Assembly.Location;
        string? hostAssembly = null;
        if (assembly.Length > 0)
        {
            var appHost = Path.ChangeExtension(assembly, null);
            if (File.Exists(appHost))
            {
                processPath = appHost;
            }
            else
            {
                processPath = Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "../../../dotnet"));
                hostAssembly = assembly;
            }
        }

        var wrapped = new ProcessStartInfo(processPath)
        {
            UseShellExecute = false,
            CreateNoWindow = info.CreateNoWindow,
            WorkingDirectory = info.WorkingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = info.RedirectStandardOutput,
            RedirectStandardError = info.RedirectStandardError,
            StandardOutputEncoding = info.StandardOutputEncoding,
            StandardErrorEncoding = info.StandardErrorEncoding
        };
        if (hostAssembly is not null)
            wrapped.ArgumentList.Add(hostAssembly);
        wrapped.ArgumentList.Add(ChildArgument);
        wrapped.ArgumentList.Add(info.FileName);
        foreach (var argument in info.ArgumentList)
            wrapped.ArgumentList.Add(argument);
        wrapped.Environment.Clear();
        foreach (var (name, value) in info.Environment)
            wrapped.Environment[name] = value;

        return wrapped;
    }

    internal static bool TryRunChild(string[] args)
    {
        if (args.Length == 0 || args[0] != ChildArgument)
            return false;

        Environment.ExitCode = 1;
        if (!OperatingSystem.IsLinux() || args.Length < 2 || CreateSession() < 0)
        {
            Console.Error.WriteLine("Could not initialize the internal media supervisor.");
            return true;
        }

        Console.Out.WriteLine(GroupReady);
        Console.Out.Flush();
        var instruction = Console.In.ReadLine();
        if (instruction is not (StartChild or StartRegistration))
            return true;
        var registration = instruction == StartRegistration;
        if (registration && SetSubreaper(PrSetChildSubreaper, 1, 0, 0, 0) < 0)
        {
            Console.Error.WriteLine("Could not track client registration descendants.");
            return true;
        }

        try
        {
            var launch = new ProcessStartInfo(args[1]) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = registration };
            foreach (var argument in args.AsSpan(2))
                launch.ArgumentList.Add(argument);
            using var process = Process.Start(launch) ?? throw new InvalidOperationException($"Unable to start '{args[1]}'.");
            if (registration)
            {
                // EOF also releases the lease when the outer CLI exits before async cancellation finishes.
                // Signal our own group so we cannot hit a reused group ID after the supervisor exits.
                new Thread(() =>
                {
                    try { Console.In.Read(); }
                    finally { _ = Signal(0, SigKill); }
                }) { IsBackground = true }.Start();
                process.StandardInput.Close();
            }
            process.WaitForExit();
            if (registration)
            {
                // Orphans are reparented here even when they close all inherited output descriptors.
                while (WaitForChild(-1, out _, 0) > 0 || Marshal.GetLastPInvokeError() == Interrupted) { }
                if (Marshal.GetLastPInvokeError() != NoChildProcesses)
                    _ = Signal(0, SigKill);
            }
            Environment.ExitCode = process.ExitCode;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Console.Error.WriteLine(ex.Message);
        }

        return true;
    }

    private static async Task<(string Text, bool Truncated)> DrainAsync(StreamReader reader, int limit, CancellationToken cancellationToken)
    {
        if (limit == 0)
        {
            await reader.BaseStream.CopyToAsync(Stream.Null, cancellationToken);
            return ("", false);
        }

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

    private static void Stop(Process process, int processGroup)
    {
        if (processGroup > 0)
            _ = Signal(-processGroup, SigKill); // Includes children after the supervised launcher exits.

        try { process.Kill(entireProcessTree: true); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }

    [DllImport("libc", EntryPoint = "setsid")]
    private static extern int CreateSession();

    [DllImport("libc", EntryPoint = "getpgid")]
    private static extern int GetProcessGroup(int processId);

    [DllImport("libc", EntryPoint = "kill")]
    private static extern int Signal(int processOrGroup, int signal);

    [DllImport("libc", EntryPoint = "prctl", SetLastError = true)]
    private static extern int SetSubreaper(int option, nuint value, nuint unused3, nuint unused4, nuint unused5);

    [DllImport("libc", EntryPoint = "waitpid", SetLastError = true)]
    private static extern int WaitForChild(int processId, out int status, int options);
}
