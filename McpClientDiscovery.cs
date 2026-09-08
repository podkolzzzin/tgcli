using System.Text.Json.Nodes;

namespace TgCli;

internal sealed class McpInstallEnvironment(string home, string os, IReadOnlyDictionary<string, string> variables)
{
    private readonly Dictionary<string, string> _variables = new(variables,
        os == "windows" ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    internal string Home { get; } = home;
    internal bool Windows => os == "windows";
    internal bool Mac => os == "macos";
    internal string Env(string key, string fallback) => _variables.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : fallback;
    internal string Xdg => Env("XDG_CONFIG_HOME", Path.Combine(Home, ".config"));
    internal string AppData => Env("APPDATA", Path.Combine(Home, "AppData", "Roaming"));
    internal string LocalAppData => Env("LOCALAPPDATA", Path.Combine(Home, "AppData", "Local"));
    internal static McpInstallEnvironment Current()
    {
        var os = OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : "linux";
        var variables = Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>()
            .ToDictionary(x => x.Key.ToString()!, x => x.Value?.ToString() ?? "", os == "windows" ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var homeKey = os == "windows" ? "USERPROFILE" : "HOME";
        var home = variables.TryGetValue(homeKey, out var configured) && !string.IsNullOrWhiteSpace(configured)
            ? configured
            : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return new(home, os, variables);
    }

    internal string? Executable(string name)
    {
        foreach (var dir in Env("PATH", "").Split(Windows ? ';' : ':', StringSplitOptions.RemoveEmptyEntries))
            foreach (var suffix in Windows ? new[] { ".exe", ".cmd", "" } : new[] { "" })
            {
                var path = Path.Combine(dir, name + suffix);
                if (File.Exists(path)) return Path.GetFullPath(path);
            }
        return null;
    }
}

internal static class McpClientDiscovery
{
    internal static IReadOnlyList<McpInstallTarget> Find(string family, McpInstallEnvironment env, string? eclipseWorkspace = null)
    {
        var targets = new List<McpInstallTarget>();
        var home = env.Home;
        if (string.IsNullOrWhiteSpace(home)) throw new InvalidOperationException("Cannot resolve the current user's home directory.");
        if (family == "claude")
        {
            var cli = env.Executable("claude");
            var claudeHome = env.Env("CLAUDE_CONFIG_DIR", Path.Combine(home, ".claude"));
            var config = env.Env("CLAUDE_CONFIG_DIR", "") is "" ? Path.Combine(home, ".claude.json") : Path.Combine(claudeHome, ".claude.json");
            targets.Add(new("Claude Code CLI", config, McpConfigFormat.Json, "mcpServers", cli is not null || File.Exists(config), cli));
            var desktopDir = env.Mac ? Path.Combine(home, "Library", "Application Support", "Claude") : env.Windows ? Path.Combine(env.AppData, "Claude") : Path.Combine(env.Xdg, "Claude");
            var desktopConfig = Path.Combine(desktopDir, "claude_desktop_config.json");
            var desktop = Directory.Exists(desktopDir) || env.Mac && (Directory.Exists("/Applications/Claude.app") || Directory.Exists(Path.Combine(home, "Applications", "Claude.app")));
            targets.Add(new("Claude Desktop", desktopConfig, McpConfigFormat.Json, "mcpServers", desktop,
                Scope: env.Mac || env.Windows ? "User scope." : "Existing Linux Desktop profile; verify compatibility with your Desktop distribution."));
        }
        else if (family == "codex")
        {
            var cli = env.Executable("codex");
            var configHome = env.Env("CODEX_HOME", Path.Combine(home, ".codex"));
            targets.Add(new("Codex CLI + Desktop", Path.Combine(configHome, "config.toml"), McpConfigFormat.Toml, "mcp_servers",
                cli is not null || Directory.Exists(configHome) || env.Mac && Directory.Exists("/Applications/Codex.app"), cli,
                Scope: "Shared user configuration for CLI and Desktop on this Codex host."));
        }
        else if (family == "copilot")
        {
            var cli = env.Executable("copilot");
            var copilotHome = env.Env("COPILOT_HOME", Path.Combine(home, ".copilot"));
            targets.Add(new("Copilot CLI / shared agent runtime", Path.Combine(copilotHome, "mcp-config.json"), McpConfigFormat.Json, "mcpServers", cli is not null || Directory.Exists(copilotHome), cli));
            Discover(() => AddVsCode(targets, env, "Code", "code", ".vscode"), "VS Code Stable", env.Xdg);
            Discover(() => AddVsCode(targets, env, "Code - Insiders", "code-insiders", ".vscode-insiders"), "VS Code Insiders", env.Xdg);
            var intellij = Path.Combine(env.Windows ? env.LocalAppData : env.Xdg, "github-copilot", "intellij");
            targets.Add(new("JetBrains Copilot (all IDEs)", Path.Combine(intellij, "mcp.json"), McpConfigFormat.Json, "servers", Directory.Exists(intellij),
                Scope: "Shared user MCP config; Copilot CLI agent runtimes also use the CLI config above."));
            var xcode = Path.Combine(home, ".config", "github-copilot", "xcode");
            targets.Add(new("Copilot for Xcode", Path.Combine(xcode, "mcp.json"), McpConfigFormat.Json, "servers", env.Mac && (Directory.Exists(xcode) || Directory.Exists("/Applications/GitHub Copilot for Xcode.app"))));
            var vs = Path.Combine(env.LocalAppData, "Microsoft", "VisualStudio");
            targets.Add(new("Visual Studio Copilot", Path.Combine(home, ".mcp.json"), McpConfigFormat.Json, "servers", env.Windows && Directory.Exists(vs)));
            Discover(() => AddEclipse(targets, env, eclipseWorkspace), "Eclipse Copilot", eclipseWorkspace ?? env.Home);
        }
        else throw new ArgumentException("MCP install target must be claude, codex or copilot.");
        return targets;

        void Discover(Action action, string client, string path)
        {
            try { action(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException)
            {
                targets.Add(new(client, path, McpConfigFormat.Json, "servers", true,
                    DiscoveryError: "Client profile discovery failed. Check access to its configuration and extension registries; other clients can still be configured."));
            }
        }
    }

    private static void AddVsCode(List<McpInstallTarget> targets, McpInstallEnvironment env, string product, string executable, string extensionHome)
    {
        var data = env.Mac ? Path.Combine(env.Home, "Library", "Application Support", product) : Path.Combine(env.Windows ? env.AppData : env.Xdg, product);
        var cli = env.Executable(executable);
        var portable = cli is null ? null : Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(cli))!, "data", "user-data");
        var portableHome = env.Env("VSCODE_PORTABLE", "");
        if (portableHome.Length > 0) portable = Path.Combine(portableHome, "user-data");
        if (portable is not null && Directory.Exists(portable)) data = portable;
        var user = Path.Combine(data, "User");
        var extensions = Path.Combine(env.Home, extensionHome, "extensions");
        if (data == portable) extensions = Path.Combine(Path.GetDirectoryName(portable!)!, "extensions");
        var copilot = Directory.Exists(extensions) && Directory.EnumerateDirectories(extensions, "github.copilot*").Any();
        targets.Add(new("VS Code " + product + " / Default", Path.Combine(user, "mcp.json"), McpConfigFormat.Json, "servers", Directory.Exists(user) && copilot,
            // File adapter targets a known profile exactly, including Desktop installs without a shell launcher.
            Scope: "Default user profile."));
        var profiles = Path.Combine(user, "profiles");
        if (!Directory.Exists(profiles)) return;
        foreach (var directory in Directory.EnumerateDirectories(profiles).Order(StringComparer.Ordinal))
        {
            var profileExtensions = Path.Combine(directory, "extensions.json");
            var installed = copilot;
            string? discoveryError = null;
            if (File.Exists(profileExtensions))
            {
                try
                {
                    var text = File.ReadAllText(profileExtensions);
                    var list = JsonNode.Parse(text)?.AsArray();
                    installed = list?.Any(x => x?["identifier"]?["id"]?.GetValue<string>().StartsWith("github.copilot", StringComparison.OrdinalIgnoreCase) == true) == true;
                }
                catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException or IOException or UnauthorizedAccessException)
                {
                    installed = true;
                    discoveryError = "Cannot read this profile's extension registry. Repair extensions.json in VS Code and retry.";
                }
            }
            targets.Add(new("VS Code " + product + " / " + Path.GetFileName(directory), Path.Combine(directory, "mcp.json"), McpConfigFormat.Json, "servers", installed, Scope: "Existing user profile.", DiscoveryError: discoveryError));
        }
    }

    private static void AddEclipse(List<McpInstallTarget> targets, McpInstallEnvironment env, string? explicitWorkspace)
    {
        var workspaces = new HashSet<string>(StringComparer.Ordinal);
        if (explicitWorkspace is not null) workspaces.Add(Path.GetFullPath(explicitWorkspace));
        var standard = Path.Combine(env.Home, "eclipse-workspace");
        if (Directory.Exists(Path.Combine(standard, ".metadata"))) workspaces.Add(standard);
        // Read the IDE's own recent-workspace registry, never recursively scan project directories.
        foreach (var root in new[] { Path.Combine(env.Home, ".eclipse"), Path.Combine(env.Home, "eclipse", "configuration") })
            foreach (var path in FindFiles(root, "org.eclipse.ui.ide.prefs", 5))
            {
                var recent = JavaProperties.Get(File.ReadAllText(path), "RECENT_WORKSPACES");
                if (recent is not null) foreach (var workspace in recent.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                        if (Path.IsPathFullyQualified(workspace) && Directory.Exists(Path.Combine(workspace, ".metadata"))) workspaces.Add(workspace);
            }
        if (workspaces.Count == 0)
            targets.Add(new("Eclipse Copilot", Path.Combine(env.Home, "eclipse-workspace", ".metadata", ".plugins", "org.eclipse.core.runtime", ".settings", "com.microsoft.copilot.eclipse.ui.prefs"), McpConfigFormat.EclipsePreferences, "servers", false,
                Scope: "Use --eclipse-workspace for a workspace absent from Eclipse's recent-workspace registry."));
        foreach (var workspace in workspaces.Order(StringComparer.Ordinal))
        {
            var plugins = Path.Combine(workspace, ".metadata", ".plugins");
            var path = Path.Combine(plugins, "org.eclipse.core.runtime", ".settings", "com.microsoft.copilot.eclipse.ui.prefs");
            var installed = File.Exists(path) || Directory.Exists(Path.Combine(plugins, "com.microsoft.copilot.eclipse.ui"));
            targets.Add(new("Eclipse Copilot / " + workspace, path, McpConfigFormat.EclipsePreferences, "servers", installed,
                Scope: "Workspace scope (Eclipse stores these preferences per workspace). Reload Eclipse preferences after installation."));
        }
    }
    private static IEnumerable<string> FindFiles(string root, string name, int depth)
    {
        if (depth < 0 || !Directory.Exists(root)) yield break;
        foreach (var file in Directory.EnumerateFiles(root, name)) yield return file;
        foreach (var dir in Directory.EnumerateDirectories(root))
        {
            if (new DirectoryInfo(dir).LinkTarget is not null) continue;
            foreach (var file in FindFiles(dir, name, depth - 1)) yield return file;
        }
    }
}
