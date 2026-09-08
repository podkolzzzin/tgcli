using System.Diagnostics;
using System.Text.Json;

namespace TgCli;

internal sealed record McpInstallTarget(string Client, string Path, McpConfigFormat Format, string RootKey,
    bool Installed, string? Cli = null, string? Profile = null, string? Scope = null, string? DiscoveryError = null);
internal sealed record McpInstallResult(string Client, string Status, string Path, string Message);
internal sealed record McpProcessResult(int ExitCode, string Output, string Error);

internal interface IMcpProcessRunner
{
    Task<McpProcessResult> RunAsync(string executable, IReadOnlyList<string> arguments, CancellationToken cancellationToken);
}

internal sealed class McpProcessRunner : IMcpProcessRunner
{
    public async Task<McpProcessResult> RunAsync(string executable, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        if (OperatingSystem.IsWindows() && Path.GetExtension(executable).Equals(".cmd", StringComparison.OrdinalIgnoreCase))
        {
            // npm launchers use cmd.exe. Reject characters expanded even inside cmd quotes.
            var parts = new[] { executable }.Concat(arguments).ToArray();
            if (parts.Any(x => x.IndexOfAny(['"', '%', '!', '\r', '\n']) >= 0))
                throw new InvalidOperationException("This Windows .cmd launcher cannot safely represent these arguments. Use the client's native .exe or remove shell expansion characters from paths.");
            start.FileName = Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe";
            start.Arguments = "/d /s /c \"" + string.Join(' ', parts.Select(x => "\"" + x + "\"")) + "\"";
        }
        else foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("Could not start MCP configuration command.");
        process.StandardInput.Close();
        var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken);
            return new(process.ExitCode, await output, await error);
        }
        catch
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            try { await Task.WhenAll(output, error); } catch (OperationCanceledException) { }
            throw;
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
                var original = File.Exists(path) ? await File.ReadAllTextAsync(path, token) : null;
                var expected = McpConfiguration.Merge(original, target.Format, target.RootKey, launch, target.Client.StartsWith("Copilot CLI", StringComparison.Ordinal));
                if (expected == original)
                    result = new(target.Client, "already-installed", path, "The saved command and arguments already match. " + target.Scope);
                else if (dryRun)
                    result = new(target.Client, "would-install", path, "Configuration validated; no files changed. " + target.Scope);
                else
                {
                    if (target.Cli is not null)
                    {
                        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                        timeout.CancelAfter(TimeSpan.FromSeconds(30));
                        var args = BuildArguments(target, launch);
                        try
                        {
                            // Validate the exact config before invoking a client, which owns its own write strategy.
                            if (original is not null) await McpConfiguration.BackupAsync(path, original, token);
                            var cliResult = await runner.RunAsync(target.Cli, args, timeout.Token);
                            if (cliResult.ExitCode != 0)
                                throw new IOException($"Client MCP registration exited with code {cliResult.ExitCode}. Check its MCP settings and supported CLI version.");
                        }
                        catch (OperationCanceledException) when (!token.IsCancellationRequested)
                        {
                            if (!await IsSavedAsync(path, target, launch, token)) throw new TimeoutException("Client configuration command timed out; the expected entry was not saved.");
                        }
                    }
                    else await McpConfiguration.WriteAtomicAsync(path, original, expected, token);
                    if (!await IsSavedAsync(path, target, launch, token)) throw new IOException("Client configuration verification failed: the expected tgcli entry was not persisted at this path.");
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
    private static async Task<bool> IsSavedAsync(string path, McpInstallTarget target, McpLaunch launch, CancellationToken ct) =>
        File.Exists(path) && launch.Matches(McpConfiguration.ReadEntry(await File.ReadAllTextAsync(path, ct), target.Format, target.RootKey));

    internal static string[] BuildArguments(McpInstallTarget target, McpLaunch launch)
    {
        if (target.Client.StartsWith("VS Code", StringComparison.Ordinal))
        {
            var json = launch.ToJson(); json["name"] = "tgcli";
            return target.Profile is null ? ["--add-mcp", json.ToJsonString()] : ["--profile", target.Profile, "--add-mcp", json.ToJsonString()];
        }
        var args = target.Client == "Claude Code CLI"
            ? new List<string> { "mcp", "add", "--transport", "stdio", "--scope", "user", "tgcli", "--", launch.Command }
            : new List<string> { "mcp", "add", "tgcli", "--", launch.Command };
        args.AddRange(launch.Arguments);
        return args.ToArray();
    }
}
