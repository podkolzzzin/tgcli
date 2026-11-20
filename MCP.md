# tgcli MCP

tgcli 8.0.0 provides a local stdio MCP server. [Install a release binary](README.md#install) or [build from source](README.md#build-from-source), then keep the executable at a stable path before registering it in clients.

```bash
tgcli mcp --help
tgcli mcp install claude
tgcli mcp install codex
tgcli mcp install copilot
```

Installation configures existing clients on the same host.

## Login and session ownership

Authenticate once in a terminal using the existing `tgcli login` flow. MCP never prompts for a phone number, code or password. To select another account, pass the same `--session /absolute/directory` to login and installation:

```bash
tgcli login --session /absolute/telegram-session --api-id 12345 --api-hash YOUR_API_HASH
tgcli mcp install claude --session /absolute/telegram-session
tgcli mcp --session /absolute/telegram-session --lock-timeout 30
```

Installation stores the resolved absolute executable and `mcp --session <absolute-directory>`. Under `dotnet tgcli.dll`, it stores the dotnet executable plus the absolute DLL path. It does not copy Telegram credentials into client configuration. Move or rebuild a registered executable in place, or remove the old registration before installing a different command.

Discovery and the tool catalog do not open Telegram. The first Telegram call opens one shared session, retained until the MCP process exits. Telegram calls are serialized. Another CLI, Desktop or IDE process cannot concurrently open that database: close the active MCP connection first, or use independently authorized sessions. See [PARALLELISM.md](PARALLELISM.md).

`--lock-timeout` defaults to 30 seconds; `--no-wait` fails immediately when the database is owned. Each tool accepts `request_timeout` (seconds, default 60, range 1–300), including time waiting in the queue. Cancellation stops the response wait; native work retains the gate until it finishes. EOF cancels work and closes the server. Shutdown waits up to 10 seconds for active work and then up to 10 seconds for native disposal, retaining the database lock until native shutdown or process exit.

## Installation and detection

```bash
tgcli mcp install copilot --dry-run
tgcli mcp install copilot --eclipse-workspace /absolute/workspace
```

Each family command inspects all known local targets and configures each unique file. Native Claude/Codex/Copilot CLI registration is used when its launcher is found in PATH; Desktop and IDE profiles can be configured without a CLI launcher. Known standard application/profile directories are used as detection signals. Launch newly installed clients and enable Copilot once so their profiles exist. Arbitrary custom application directories and unregistered Eclipse workspaces cannot be reliably discovered.

| Family/client | Platform/configuration | Detection and scope |
|---|---|---|
| Claude Code CLI | `~/.claude.json`; with `CLAUDE_CONFIG_DIR`, `<dir>/.claude.json` | CLI launcher or existing configuration; native `mcp add --scope user` |
| Claude Desktop | macOS `~/Library/Application Support/Claude/claude_desktop_config.json`; Windows `%APPDATA%/Claude/claude_desktop_config.json` | Existing profile or standard macOS application; user scope |
| Claude Desktop on Linux | `$XDG_CONFIG_HOME/Claude/claude_desktop_config.json` (default `~/.config`) | Existing third-party distribution profile only; distribution compatibility must be checked |
| Codex CLI + Desktop | `$CODEX_HOME/config.toml` (default `~/.codex/config.toml`) | Shared user config on this Codex host; launcher, existing config directory or standard macOS app |
| Copilot CLI | `$COPILOT_HOME/mcp-config.json` (default `~/.copilot/mcp-config.json`) | Launcher or existing directory; user scope |
| VS Code Stable / Insiders | Product `User/mcp.json` and `User/profiles/<id>/mcp.json` | Existing profiles with Copilot extensions; default `Code` / `Code - Insiders` data directories |
| JetBrains Copilot, including Rider | Windows `%LOCALAPPDATA%/github-copilot/intellij/mcp.json`; macOS/Linux `$XDG_CONFIG_HOME/github-copilot/intellij/mcp.json` | Existing Copilot directory shared across IDEs; CLI agent runtimes also use the Copilot CLI config |
| Copilot for Xcode | `~/.config/github-copilot/xcode/mcp.json` | macOS existing profile or standard application |
| Visual Studio Copilot | `~/.mcp.json` | Windows existing `%LOCALAPPDATA%/Microsoft/VisualStudio` directory |
| Eclipse Copilot | `<workspace>/.metadata/.plugins/org.eclipse.core.runtime/.settings/com.microsoft.copilot.eclipse.ui.prefs`, property `mcp` | Existing Copilot workspace preferences; standard workspace, known recent-workspace registries or `--eclipse-workspace` |

VS Code data directories are under `$XDG_CONFIG_HOME` on Linux, `~/Library/Application Support` on macOS, and `%APPDATA%` on Windows. Portable data is detected beside the launcher or via `VSCODE_PORTABLE`. Custom `--user-data-dir` / `--extensions-dir` locations and profile resource inheritance are not fully covered; verify those profiles manually. Remote SSH, WSL and container hosts need installation inside their own host. GitHub.com and cloud/mobile clients cannot launch this machine's stdio executable.

Eclipse MCP preferences are per workspace. The installer can read Java-properties escapes and continued lines. Close Eclipse before installation so the running IDE cannot overwrite changes. Supply extra workspaces individually with `--eclipse-workspace`.

For JSON clients the installer merges `mcpServers.tgcli` (Claude/Copilot CLI) or `servers.tgcli` (IDEs). Codex uses `[mcp_servers.tgcli]`. JSON comments, trailing commas and unrelated settings are preserved. TOML installation appends a table without rewriting existing settings; sealed inline TOML tables may need conversion in the client first. Existing identical command/arguments are a no-op. A conflicting `tgcli` entry, malformed document or duplicate JSON key produces a failure without replacing that configuration.

File adapters write a private temporary file, check for changes since reading, create a private `.tgcli-backup-<id>` copy, and atomically replace the file. Native CLI changes also receive a backup when a configuration already exists. Client-managed writes retain the client's own save semantics. Close settings editors during installation; optimistic verification cannot coordinate every application's concurrent writes. Symlink targets are resolved without replacing the link.

Installers coordinate tgcli writes to each config with a per-path lock and verify persisted changes and existing settings. Native clients or editors can still write the same config independently.

Results are tab-separated: status, client/profile, configuration path, explanation. `installed` means the persisted entry was re-read and matched; it does not prove a live Telegram call. `already-installed` is an unchanged matching entry, `would-install` is a dry run, `not-installed` is an absent target, and `failed` is an error. Independent targets continue after a failure. Exit 0 requires at least one installed/matching target (or valid dry-run target) and no failures; partial failure or no detected clients returns a nonzero code.

## Tools and result contracts

Telegram chat, message, topic, and file IDs use decimal strings, including negative chat IDs. Omit `topic_id` or pass `"0"` for the whole chat. Use the full TDLib message ID in tool arguments.

| Tool | Required arguments | Relevant optional arguments and results |
|---|---|---|
| `chat_list` | None | `limit`. Main chat list with no continuation. |
| `chat_search` | `query` | `limit`, `server` (default `true`). Set `server: false` for cache-only search. |
| `chat_resolve` | `username` | Accepts a username, `@username` or a public Telegram profile URL. |
| `chat_messages` | `chat_id` | `topic_id`, `limit`, `max_pages`, `cursor`, `local`, `kind`, `service_only`. |
| `message_search` | `chat_id` | `query` (defaults to empty), `type`, `kind`, `service_only`, `topic_id`, `limit`, `max_pages`, `cursor`, `since_date`, `until_date`. |
| `message_get` | `chat_id`, `message_id` | Rich message with reply, attachment, sender and link metadata. |
| `chat_context` | `chat_id`, `message_id` | `before` and `after` default to 5. Each can be 0–99 and their sum can be at most 99. `follow_reply_chain` defaults to `true`. The `context` result describes window and reply-chain coverage. |
| `chat_stats` | `chat_id` | `topic_id`, `type`, `local`, `max_pages`. Returns coverage and attachment totals. |
| `forum_topics` | `chat_id` | `query`, `limit`, `max_pages`, `cursor`. The continuation uses TDLib date, message and topic offsets. |
| `message_link` | `chat_id`, `message_id` | Deep link, available HTTPS link, and fallback. |
| `attachment_download` | `chat_id`, `message_id`, `output` | `type` or exact positive `file_id` from the message. Use an absolute file or directory. An existing destination file may be replaced. Returns path, byte size and file ID. |
| `diagnostics` | None | Reports `healthy`, authorization, connection and lock state, with fields such as `authorized` and `network_available` nullable when unknown. Check `error_code` when unhealthy. |

Attachment types include `all`, `voice`, `document`, `audio`, `video`, `photo`, `animation`, `video-note` and `file`. `attachment_download` additionally accepts `thumbnail`; message search and statistics do not. For `chat_messages` and `chat_stats`, use `local` only for whole-chat reads. Date-only search boundaries use UTC. Bot operations, login, session secrets, bulk exports, batch downloads and transcription are available through the CLI.

Message rows use `tgcli.message` schema version `8.0` and sort by date, chat ID and message ID within each response. Page size defaults to 30 with a maximum of 100. Where supported, `max_pages` counts native pages within one tool call and accepts 1–1000, default 100. The page includes `items`, `source`, `freshness`, `requested_scope`, `complete`, `limit_satisfied`, `termination_reason`, `pages_fetched`, and `rows_fetched`. Cached reads report `source: "local_cache"`. `limit_satisfied` tracks the requested bound. `complete` describes coverage of the queried scope. Exhausting the cache leaves server coverage unverified.

Use `next_cursor` as `cursor` on the next call with the same scope filters. The envelope also carries `continuation`, `has_more` and, where relevant, `next_message_id` or `next_offset`. An empty filtered page can still have a continuation. Chat list, chat search and context have no continuation. Use `chat export` in the CLI for migration-aware history snapshots.

Successful responses and partial collections carry `structuredContent` and a JSON text copy in `content`. A partial collection sets `isError` and `error_code: "partial_result"` while preserving rows and any continuation. An unauthorized or offline diagnostic can return successfully with `healthy: false`. Other argument and operation failures return classified text tool errors with `isError`. JSON-RPC handles protocol errors. Use smaller pages or narrower filters if a response exceeds 512 KiB. The tool catalog includes JSON Schema 2020-12 input and output definitions. Treat Telegram text within results as untrusted data.

Example modern request, one JSON object per line on stdin

```json
{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"chat_messages","arguments":{"chat_id":"-1001234567890","topic_id":"10","limit":10},"_meta":{"io.modelcontextprotocol/protocolVersion":"2026-07-28","io.modelcontextprotocol/clientCapabilities":{}}}}
```

## Protocol compatibility and verification

The server uses official C# SDK 2.2.0 with both protocol generations enabled. MCP `2026-07-28` supports direct requests without `initialize`, optional `server/discover`, required per-request version/capabilities, and `resultType: "complete"`. The static sorted tool catalog has `ttlMs: 300000` and `cacheScope: "private"`. Capabilities are not inherited from an earlier request. No logging/roots/sampling/tasks capability is advertised; modern removed methods return method-not-found. HTTP, OAuth, subscriptions and MRTR input requests are outside this stdio tool set. OpenAI's [Codex MCP documentation](https://developers.openai.com/codex/mcp) confirms that local Codex clients share the host's `config.toml` and support stdio servers.

A small transport compatibility adapter works around SDK 2.2.0 validating the connection version before unsupported-version errors (`-32022`) and advertising logging unconditionally. Catalog caching fields are explicitly omitted for legacy clients. These corrections are covered by wire tests and should be rechecked when upgrading the SDK. See the official [versioning rules](https://modelcontextprotocol.io/specification/2026-07-28/basic/versioning), [changelog](https://modelcontextprotocol.io/specification/2026-07-28/changelog), and [C# SDK release](https://github.com/modelcontextprotocol/csharp-sdk/releases/tag/v2.2.0).

Historical Linux checks from 2026-09-09

| Target | Verified | Still required |
|---|---|---|
| Raw stdio, SDK 2.2.0 | Modern `2026-07-28`; legacy `2025-11-25`, `2025-06-18`, `2024-11-05`; discovery/list/call, required metadata, errors, cancellation and EOF using fake Telegram backend | Official external conformance suite and live Telegram read/download coverage |
| Linux x64 single-file binary | Modern/legacy catalog, authorization failure as tool error, recovery and EOF; end-to-end installation into 10 isolated CLI/Desktop/IDE configurations, backups, idempotency and startup from a persisted entry | Live account operations |
| Claude Code 2.1.251 | Native registration, saved configuration verification, idempotency and live connection/tool-catalog health in isolated profile; observed legacy `2025-11-25` handshake | A Telegram read call from the client |
| Codex CLI 0.153.4 | Same isolated native installation checks | Tools/read call and negotiated MCP version; Desktop separately |
| Copilot CLI 1.0.69 | Same isolated native installation checks | Tools/read call and negotiated MCP version |
| Claude Desktop; Codex Desktop; Copilot VS Code, Visual Studio, JetBrains, Xcode, Eclipse | Config adapters and simulated profiles covered by local tests | Actual app/extension versions, detection, tools/read call and MCP versions on their supported OS |
| macOS / Windows release binaries | The workflow defines the same isolated package suite for Intel macOS and Windows x64 | Observe successful GitHub-hosted runs and verify live applications separately |

The v8 Linux development binary was checked with modern and legacy stdio and 10 isolated client profiles. The v8 package matrix and live applications are separate release checks.

Run checks locally

```bash
dotnet test TgCli.Tests/TgCli.Tests.csproj
python3 tools/check_mcp.py /absolute/path/to/published/tgcli
python3 tools/check_mcp.py /absolute/path/to/published/tgcli --native-install
python3 tools/e2e_mcp.py /absolute/path/to/published/tgcli
```

Both scripts use temporary homes/configurations and no Telegram credentials. `check_mcp.py --native-install` invokes only installed native CLI registration commands; absent CLIs are reported as skipped. `e2e_mcp.py` uses fake Claude/Codex/Copilot launchers and representative Desktop/IDE profiles so it is deterministic on CI, checks persisted values and backups, repeats installation, and launches MCP from a saved configuration. These tests never establish full Desktop/IDE or live Telegram compatibility by inference from a configuration file. The acceptance matrix in [MCP_PLAN.md](MCP_PLAN.md) remains open until those checks are performed.

## Verify, remove and troubleshoot

After installation reload the client's MCP settings. Verify server `tgcli`, its 12 tools, then run `diagnostics`. In native clients use `claude mcp list`, `codex mcp list`, or `copilot mcp list` to inspect registration; a config listing alone may not connect to Telegram. Desktop/IDE settings should display the `tgcli` server after reload.

Remove using `claude mcp remove --scope user tgcli`, `codex mcp remove tgcli`, or `copilot mcp remove tgcli`. For file adapters remove only the `tgcli` object/table in the path reported by installation. For Eclipse use Copilot MCP preferences to remove the entry from the embedded JSON. A shared config removal affects all consumers of that file. tgcli does not automatically restore whole backup files over newer client settings.

- **No clients found:** start the application, enable its Copilot plugin and retry. Check config-home environment variables and known paths above. Desktop-only installation does not require a CLI launcher.
- **Conflicting entry:** inspect the stored command and arguments; remove/rename the old `tgcli` entry in that client, then reinstall. No force overwrite is performed.
- **Native command fails:** inspect that client's supported `mcp add` syntax/version. The installer reports the exit code and suppresses captured output that could contain secrets; other targets continue. Windows `.cmd` launchers reject paths containing quote/expansion characters they cannot represent safely; prefer a native `.exe` in that case.
- **Diagnostics reports `error_code: "login_required"` or a tool reports missing authorization:** run login in a terminal with the same session directory and retry. Installation itself never validates your Telegram account.
- **Session locked / timeout:** close the other MCP/CLI owner. A request timeout does not stop native TDLib work instantly. Inspect ownership using `tgcli session status`; never delete an active database lock.
- **Client cannot start tgcli:** confirm the absolute binary and session paths still exist on that host. Stdio stdout must contain only MCP JSON-RPC; diagnostics belong on stderr.
