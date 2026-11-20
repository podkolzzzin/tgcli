using System.Diagnostics;
using System.Text.Json;

namespace TgCli;

internal enum McpClientKind { File, Claude, Codex, Copilot }
internal sealed record McpInstallTarget(string Client, string Path, McpConfigFormat Format, string RootKey,
    bool Installed, string? Cli = null, McpClientKind Kind = McpClientKind.File, string? Scope = null, string? DiscoveryError = null);
internal sealed record McpInstallResult(string Client, string Status, string Path, string Message);

internal interface IMcpProcessRunner
{
    Task<int> RunAsync(string executable, IReadOnlyList<string> arguments, CancellationToken cancellationToken);
}

internal sealed class McpProcessRunner : IMcpProcessRunner
{
    public async Task<int> RunAsync(string executable, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        if (OperatingSystem.IsWindows() && Path.GetExtension(executable).Equals(".cmd", StringComparison.OrdinalIgnoreCase))
        {
            start.FileName = Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe";
            start.Arguments = BuildWindowsCmdArguments(executable, arguments);
        }
        else foreach (var argument in arguments) start.ArgumentList.Add(argument);
        // Native clients must finish config writes before exiting. Linux also supervises descendants
        // in the launch group. Other platforms can only attempt cleanup of the visible process tree.
        return await MediaProcess.RunRegistrationAsync(start, cancellationToken);
    }

    internal static string BuildWindowsCmdArguments(string executable, IReadOnlyList<string> arguments)
    {
        // npm launchers use cmd.exe and pass %* to their native executable.
        var parts = new[] { executable }.Concat(arguments).ToArray();
        if (parts.Any(x => x.IndexOfAny(['"', '%', '!', '\r', '\n', '\0']) >= 0))
            throw new InvalidOperationException("This Windows .cmd launcher cannot safely represent these arguments. Use the client's native .exe or remove shell expansion characters from paths.");
        return "/d /v:off /s /c \"" + string.Join(' ', parts.Select(Quote)) + "\"";

        static string Quote(string argument)
        {
            // Preserve trailing backslashes when the shim forwards the quoted argument to an .exe.
            var trailing = argument.Length - argument.TrimEnd('\\').Length;
            return "\"" + argument + new string('\\', trailing) + "\"";
        }
    }
}

internal sealed class McpInstaller(IMcpProcessRunner runner)
{
    internal async Task<IReadOnlyList<McpInstallResult>> InstallAsync(IEnumerable<McpInstallTarget> targets, McpLaunch launch, bool dryRun, CancellationToken token)
    {
        var results = new List<McpInstallResult>();
        var completedPaths = new Dictionary<string, McpInstallResult>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var target in targets)
        {
            token.ThrowIfCancellationRequested();
            if (!target.Installed) { results.Add(new(target.Client, "not-installed", target.Path, "Client/profile not detected.")); continue; }
            var path = target.Path;
            McpInstallResult result;
            try
            {
                if (target.DiscoveryError is not null) throw new InvalidDataException(target.DiscoveryError);
                path = CanonicalPath(path);
                if (completedPaths.TryGetValue(path, out var previous))
                {
                    results.Add(previous with { Client = target.Client, Message = $"Shares configuration with {previous.Client}. {previous.Message}" });
                    continue;
                }
                var original = await McpConfiguration.ReadAsync(path, target.Format, token);
                var expected = McpConfiguration.Merge(original, target.Format, target.RootKey, launch, target.Kind == McpClientKind.Copilot);
                if (expected != original && !launch.Matches(McpConfiguration.ReadEntry(expected, target.Format, target.RootKey)))
                    throw new InvalidDataException("The generated configuration did not contain the expected tgcli entry.");
                if (expected == original)
                    result = new(target.Client, "already-installed", path, "The saved command and arguments already match. " + target.Scope);
                else if (dryRun)
                    result = new(target.Client, "would-install", path, "Configuration validated; no files changed. " + target.Scope);
                else
                {
                    if (target.Cli is not null)
                        await InstallWithClientAsync(path, original, target, launch, token);
                    else await McpConfiguration.WriteAtomicAsync(path, original, expected, token, target.Format);
                    result = new(target.Client, "installed", path, "Saved and verified. Reload MCP settings in the client. " + target.Scope);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Parser diagnostics may embed configuration secrets, so never forward their full text.
                var message = ex is JsonException or Tomlyn.TomlException ? "Configuration could not be parsed. Repair it in the client before retrying." : ex.Message;
                result = new(target.Client, "failed", path, message);
            }
            completedPaths[path] = result;
            results.Add(result);
        }
        return results;
    }

    private static string CanonicalPath(string path)
    {
        path = Path.GetFullPath(path);
        var root = Path.GetPathRoot(path)!;
        var resolved = root;
        foreach (var part in path[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            resolved = Path.Combine(resolved, part);
            FileSystemInfo info = Directory.Exists(resolved) ? new DirectoryInfo(resolved) : new FileInfo(resolved);
            if (info.Exists && info.ResolveLinkTarget(true) is { } target) resolved = target.FullName;
        }
        return resolved;
    }
    private async Task InstallWithClientAsync(string path, string? original, McpInstallTarget target, McpLaunch launch, CancellationToken token)
    {
        var args = BuildArguments(target, launch);
        token.ThrowIfCancellationRequested();
        using var writeLock = McpConfiguration.AcquireWriteLock(path);
        await McpConfiguration.EnsureUnchangedAsync(path, original, target.Format, token);
        if (original is not null)
        {
            await McpConfiguration.BackupAsync(path, original, target.Format, token);
            await McpConfiguration.EnsureUnchangedAsync(path, original, target.Format, token);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            var exitCode = await runner.RunAsync(target.Cli!, args, timeout.Token);
            if (exitCode != 0)
                throw new IOException($"Client MCP registration exited with code {exitCode}. Check its MCP settings and supported CLI version.");
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new TimeoutException("Client configuration command timed out. Registration may have changed the configuration. Review the saved MCP settings before retrying.");
        }

        var saved = await McpConfiguration.ReadAsync(path, target.Format, token);
        if (saved is null || !launch.Matches(McpConfiguration.ReadEntry(saved, target.Format, target.RootKey)))
            throw new IOException("Client configuration verification failed: the expected tgcli entry was not persisted at this path.");
        if (original is not null && !McpConfiguration.PreservesSettings(original, saved, target.Format))
            throw new IOException("Client configuration verification failed: unrelated saved settings changed. Review the private backup before retrying.");
    }

    private static string[] BuildArguments(McpInstallTarget target, McpLaunch launch)
    {
        var prefix = target.Kind switch
        {
            McpClientKind.Claude => new[] { "mcp", "add", "--transport", "stdio", "--scope", "user", "tgcli", "--" },
            McpClientKind.Codex or McpClientKind.Copilot => ["mcp", "add", "tgcli", "--"],
            _ => throw new InvalidOperationException("This adapter does not support native registration.")
        };
        return [..prefix, launch.Command, ..launch.Arguments];
    }
}
