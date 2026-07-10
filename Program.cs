using ConsoleAppFramework;
using System.Reflection;
using System.Runtime.InteropServices;
using TdLib;

namespace TgCli;

internal static class Program
{
    public static void Main(string[] args)
    {
        NativeTdLibBootstrap.Configure();

        var app = ConsoleApp.Create();
        ConsoleApp.LogError = static message => Console.Error.WriteLine(message);
        app.UseFilter<FriendlyErrorFilter>();

        app.Add<TgCommands>();
        app.Add<ChatCommands>("chat");
        app.Add<ChannelCommands>("channel");
        app.Add<LinkCommands>("link");
        app.Add<MessageCommands>("message");

        app.Run(args);
    }
}

internal sealed class FriendlyErrorFilter(ConsoleAppFilter next) : ConsoleAppFilter(next)
{
    public override async Task InvokeAsync(ConsoleAppContext context, CancellationToken cancellationToken)
    {
        try
        {
            await Next.InvokeAsync(context, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            Environment.ExitCode = 130;
            Console.Error.WriteLine("Operation canceled.");
        }
        catch (Exception ex)
        {
            Environment.ExitCode = 1;
            Console.Error.WriteLine(FriendlyMessage(ex));
        }
    }

    private static string FriendlyMessage(Exception ex)
    {
        var message = ex switch
        {
            TargetInvocationException { InnerException: not null } tie => FriendlyMessage(tie.InnerException!),
            AggregateException { InnerExceptions.Count: 1 } aggregate => FriendlyMessage(aggregate.InnerExceptions[0]),
            _ => ex.Message
        };

        return string.IsNullOrWhiteSpace(message)
            ? ex.GetType().Name
            : message;
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
