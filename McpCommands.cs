using ConsoleAppFramework;

namespace TgCli;

public sealed class McpCommands
{
    /// <summary>Install tgcli MCP in all detected local clients of a family: claude, codex or copilot.</summary>
    /// <param name="target">Client family: claude, codex or copilot.</param>
    /// <param name="session">Telegram session directory to use from every client.</param>
    /// <param name="dryRun">Inspect and validate installation without changing client configuration.</param>
    /// <param name="eclipseWorkspace">Additional Eclipse workspace to configure for Copilot.</param>
    public async Task Install([Argument] string target, string? session = null, bool dryRun = false, string? eclipseWorkspace = null, CancellationToken cancellationToken = default)
    {
        var environment = McpInstallEnvironment.Current();
        var targets = McpClientDiscovery.Find(target.ToLowerInvariant(), environment, eclipseWorkspace);
        var launch = McpLaunch.Current(TelegramSession.ResolveSessionDirectory(session));
        var results = await new McpInstaller(new McpProcessRunner()).InstallAsync(targets, launch, dryRun, cancellationToken);
        foreach (var result in results)
            Console.WriteLine($"{result.Status}\t{result.Client}\t{result.Path}\t{result.Message}");
        if (results.Any(x => x.Status is "installed" or "already-installed" or "would-install"))
        {
            Console.WriteLine("Server: tgcli; launch: " + System.Text.Json.JsonSerializer.Serialize(new { command = launch.Command, args = launch.Arguments }));
            Console.WriteLine("Reload the client's MCP settings and check that tgcli exposes 12 tools. Run diagnostics to verify Telegram authorization. Login separately in a terminal if required.");
        }
        if (results.Any(x => x.Status is "failed" or "unsupported") || !results.Any(x => x.Status is "installed" or "already-installed" or "would-install"))
            Environment.ExitCode = 1;
    }

    /// <summary>Run a local MCP server over stdin/stdout. Login separately before using Telegram tools.</summary>
    [Command("")]
    public async Task Run(string? session = null, int lockTimeout = 30, bool noWait = false, CancellationToken cancellationToken = default)
    {
        if (lockTimeout < 0) throw new ArgumentException("--lock-timeout must be zero or greater.");
        await using var backend = new McpTelegramBackend(session, lockTimeout, noWait);
        await McpServerHost.RunAsync(backend, cancellationToken);
    }
}
