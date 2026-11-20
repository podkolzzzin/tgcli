using ConsoleAppFramework;
using System.Reflection;
using System.Runtime.InteropServices;
using TdLib;

namespace TgCli;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (MediaProcess.TryRunChild(args))
            return Environment.ExitCode;

        var app = ConsoleApp.Create();
        ConsoleApp.LogError = static message => Console.Error.WriteLine(CliFailure.BoundedMessage(message));
        app.UseFilter<CliErrorFilter>();

        app.Add<TgCommands>();
        app.Add<ChatCommands>("chat");
        app.Add<ChannelCommands>("channel");
        app.Add<BotCommands>("bot");
        app.Add<ForumCommands>("forum");
        app.Add<LinkCommands>("link");
        app.Add<MessageCommands>("message");
        app.Add<SessionCommands>("session");
        app.Add<McpCommands>("mcp");

        using var canceled = new CancellationTokenSource();
        using var interrupt = PosixSignalRegistration.Create(PosixSignal.SIGINT, Cancel);
        using var terminate = PosixSignalRegistration.Create(PosixSignal.SIGTERM, Cancel);
        using var quit = PosixSignalRegistration.Create(PosixSignal.SIGQUIT, Cancel);
        try
        {
            var preflight = new CliPreflight(
                (typeof(TgCommands), ""), (typeof(ChatCommands), "chat"), (typeof(ChannelCommands), "channel"),
                (typeof(BotCommands), "bot"), (typeof(ForumCommands), "forum"), (typeof(LinkCommands), "link"),
                (typeof(MessageCommands), "message"), (typeof(SessionCommands), "session"), (typeof(McpCommands), "mcp"));
            var invocation = preflight.Prepare(args);
            Console.SetOut(CliOutputWriter.Open());
            NativeTdLibBootstrap.Configure();

            var diagnostics = invocation.DiagnosticRoot is { } diagnosticRoot
                ? new CliDiagnosticOutput(diagnosticRoot, Console.Out) : null;
            var failure = await CliExecution.RunAsync(async token =>
            {
                await app.RunAsync(invocation.Arguments, token);
                token.ThrowIfCancellationRequested();
                if (invocation.ShowGlobalTimeoutHelp)
                    Console.WriteLine("  --request-timeout <seconds>    Total command deadline, 1–3600 (default: 60). Excludes the MCP server.");
            }, invocation.Budget, canceled.Token, diagnostics);
            return failure is null ? Environment.ExitCode : ReportFailure(failure);
        }
        catch (Exception error)
        {
            return ReportFailure(error);
        }

        void Cancel(PosixSignalContext signal)
        {
            signal.Cancel = true;
            _ = canceled.CancelAsync();
        }
    }

    private static int ReportFailure(Exception error)
    {
        var failure = CliFailure.FromException(error);
        if (failure.Message is { Length: > 0 }) Console.Error.WriteLine(failure.Message);
        return failure.ExitCode;
    }
}

internal sealed class CliErrorFilter(ConsoleAppFilter next) : ConsoleAppFilter(next)
{
    public override async Task InvokeAsync(ConsoleAppContext context, CancellationToken cancellationToken)
    {
        try { await Next.InvokeAsync(context, cancellationToken); }
        catch (Exception error) { CliExecution.RecordFailure(error); }
    }
}

internal static class NativeTdLibBootstrap
{
    private static int _configured;

    public static void Configure()
    {
        if (Interlocked.Exchange(ref _configured, 1) != 0)
        {
            return;
        }

        try
        {
            NativeLibrary.SetDllImportResolver(typeof(TdJsonClient).Assembly, ResolveTdJson);
        }
        catch (InvalidOperationException)
        {
            // Resolver was already registered by the host/test process.
        }
    }

    private static IntPtr ResolveTdJson(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (!IsTdJson(libraryName))
        {
            return IntPtr.Zero;
        }

        foreach (var candidate in CandidatePaths())
        {
            if (File.Exists(candidate) && NativeLibrary.TryLoad(candidate, out var handle))
            {
                return handle;
            }
        }

        return NativeLibrary.TryLoad(libraryName, assembly, searchPath, out var fallbackHandle)
            ? fallbackHandle
            : IntPtr.Zero;
    }

    private static bool IsTdJson(string libraryName)
    {
        var name = Path.GetFileNameWithoutExtension(libraryName);
        return string.Equals(name, "tdjson", StringComparison.OrdinalIgnoreCase)
               || string.Equals(name, "libtdjson", StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> CandidatePaths()
    {
        var fileName = NativeLibraryFileName();
        var runtimeNativePath = Path.Combine("runtimes", RuntimeIdentifier(), "native", fileName);
        var bases = new[]
        {
            Environment.GetEnvironmentVariable("TGCLI_NATIVE_DIR"),
            AppContext.BaseDirectory,
            Path.GetDirectoryName(Environment.ProcessPath),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share", "tgcli", "app")
        };

        foreach (var basePath in bases.Where(path => !string.IsNullOrWhiteSpace(path)).Distinct())
        {
            yield return Path.Combine(basePath!, fileName);
            yield return Path.Combine(basePath!, runtimeNativePath);
        }
    }

    private static string NativeLibraryFileName()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return "tdjson.dll";
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return "libtdjson.dylib";
        }

        return "libtdjson.so";
    }

    private static string RuntimeIdentifier()
    {
        var os = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? "win"
            : RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
                ? "osx"
                : "linux";
        var arch = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.Arm64 => "arm64",
            Architecture.X64 => "x64",
            Architecture.X86 => "x86",
            _ => RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant()
        };

        return $"{os}-{arch}";
    }
}
