# Changelog

## 8.0.0 (2026-11-20)

- Synchronize online reads within the operation deadline. Explicit cache-only reads report the cache as their source.
- Use one versioned rich message schema for CLI and MCP JSON. Machine-readable IDs are decimal strings. Collection output reports acquisition scope and completeness.
- Export complete history by default and validate the account, schema and scope before updating a snapshot. Reconcile edits and verified deletions with `--incremental`.
- Classify invalid arguments, partial results, missing authorization, timeouts, and interrupted operations with distinct exit codes.
- Reuse verified batch downloads, preserving a receipt for every input row. Keep offline transcription bundled and supervise optional external transcribers on Linux.
- Verify checksums and startup before replacing an installed executable. Run the same packaged CLI, installer, MCP, and transcription checks for pull requests and releases.

Review the [v8 migration guide](https://github.com/podkolzzzin/tgcli/blob/v8.0.0/docs/v8-migration.md) before updating automation or refreshing older exports.

### Install or update

<!-- install:start -->
On Linux and macOS, use Python 3.11 or later, `curl`, and `sudo`. On Windows, use x64 Python 3.13 or later for private staging, and open PowerShell as Administrator.

Linux x64

```bash
(
  installer=$(mktemp "${TMPDIR:-/tmp}/tgcli-install.XXXXXXXXXX") || exit
  trap 'rm -f "$installer"' EXIT
  curl -fsSL --proto '=https' --proto-redir '=https' -o "$installer" "https://raw.githubusercontent.com/podkolzzzin/tgcli/v8.0.0/tools/install.py" &&
    sudo python3 -I "$installer" --tag v8.0.0 --rid linux-x64 --destination /usr/local/bin/tgcli
)
```

Windows x64

```powershell
$installer = Join-Path ([IO.Path]::GetTempPath()) ("tgcli-install-" + [Guid]::NewGuid() + ".py")
try {
  Invoke-WebRequest -UseBasicParsing -ErrorAction Stop -Uri "https://raw.githubusercontent.com/podkolzzzin/tgcli/v8.0.0/tools/install.py" -OutFile $installer
  py -3 -I $installer --tag v8.0.0 --rid win-x64 --destination "$env:ProgramFiles\tgcli\tgcli.exe"
  if ($LASTEXITCODE -ne 0) { throw "tgcli installation failed" }
} finally {
  Remove-Item -LiteralPath $installer -ErrorAction SilentlyContinue
}
```

macOS Intel

```bash
(
  installer=$(mktemp "${TMPDIR:-/tmp}/tgcli-install.XXXXXXXXXX") || exit
  trap 'rm -f "$installer"' EXIT
  curl -fsSL --proto '=https' --proto-redir '=https' -o "$installer" "https://raw.githubusercontent.com/podkolzzzin/tgcli/v8.0.0/tools/install.py" &&
    sudo python3 -I "$installer" --tag v8.0.0 --rid osx-x64 --destination /usr/local/bin/tgcli
)
```

The installer checks SHA-256, version, and help output before replacing the executable. A failed update preserves the previous installation. Stop any running tgcli or MCP process before updating. On Windows, invoke `& "$env:ProgramFiles\tgcli\tgcli.exe" --version` or add its directory to `Path`. On Linux and macOS, run `tgcli --version` from your usual shell.
<!-- install:end -->

## 7.0.1 (unreleased)

- Wait for TDLib connection/update readiness when opening an online session, including MCP reconnects.
- Continue export pagination after a short history page and report observed network state in diagnostics.

## 7.0.0 (2026-11-03)

- Add offline transcription of voice notes, video notes, audio and video with embedded multilingual Whisper base weights and a .NET-owned in-process CPU engine.
- Add `transcribe --input`, `message transcribe` and opt-in `chat export --transcribe`, with language selection and timestamped JSON segments.
- Bundle the media decoder and native runtime, verify embedded assets and keep generated transcripts separate from original captions; include them in Markdown/TSV/plain exports too.
- Preserve `--transcribe-command` and extend it to full audio/video attachments.
- Update project skills and application version to `7.0.0`.

[Release v7.0.0](https://github.com/podkolzzzin/tgcli/releases/tag/v7.0.0)


## 6.3.0 (2026-09-09)

- Add `tgcli mcp` with 12 Telegram read/search/download tools, lazy session initialization, bounded request waits, serialized native operations and stdio-only protocol output.
- Target MCP `2026-07-28` and retain legacy initialization compatibility, with raw JSON-RPC process tests for both paths.
- Add `mcp install claude|codex|copilot`, client/profile discovery, dry runs, conflict protection, backups and verification of persisted configuration.
- Include local adapters for Claude Desktop/Code, shared Codex CLI/Desktop configuration, Copilot CLI, VS Code Stable/Insiders profiles, Visual Studio, JetBrains, Xcode and known Eclipse workspaces. Desktop/IDE runtime verification remains pending; see `MCP.md`.
- Retain the Telegram database lock until native shutdown completes, including when bounded shutdown waiting expires.
- Add hermetic published-binary MCP end-to-end tests for protocol negotiation, native CLI registration, Desktop/IDE profile adapters, persisted launch commands, backups and idempotency; run them on Linux, macOS and Windows for pull requests, main and releases.
- Update the TDLib application version reported by tgcli to `6.3.0`.

[Release v6.3.0](https://github.com/podkolzzzin/tgcli/releases/tag/v6.3.0)

## 6.2.0

- Add `tgcli download-batch` to download JSONL message references concurrently through one TDLib client.
- Accept rows from filtered `chat search`, `chat messages`, and `chat export` output using stable `chat_id` and `message_id` references.
- Add bounded `--parallel` scheduling, collision-resistant output names, stable result ordering, and per-file JSONL errors.
- Update the TDLib application version reported by tgcli to `6.2.0`.

[Release v6.2.0](https://github.com/podkolzzzin/tgcli/releases/tag/v6.2.0)

## 6.1.0

- Add `tgcli forum topics` to list forum topic names and stable topic ids.
- Add `--topic-id` to `chat messages` and `chat search`.
- Add topic-aware `chat stats --type <kind>` with attachment byte totals, known/unknown size counts, unique-file totals, pagination evidence, and explicit completeness.
- Add bounded forum request timeouts and cursor-stall protection so topic history cannot hang indefinitely or silently loop.
- Fix session lock leaks when TDLib initialization or authorization readiness fails, bound TDLib disposal, and remove owned lock metadata after normal shutdown.
- Add `tgcli session status` and safe `tgcli session unlock --stale-only`; active operating-system locks are never overridden.
- Update the TDLib application version reported by tgcli to `6.1.0`.

[Release v6.1.0](https://github.com/podkolzzzin/tgcli/releases/tag/v6.1.0)

## 6.0.0

- Add `tgcli bot create`, `tgcli bot remove`, `tgcli bot list`, and `tgcli bot token` for BotFather-backed managed bot workflows.
- Add `tgcli bot write` to send messages to bots, print recent bot messages with UI metadata, and press inline callback buttons by text or `row,column`.
- Update the TDLib application version reported by tgcli to `6.0.0`.

[Release v6.0.0](https://github.com/podkolzzzin/tgcli/releases/tag/v6.0.0)

## 5.1.0

- Add `tgcli session export` to write a compact single-line Telegram authorization secret.
- Add `tgcli session import` to restore that authorization state from stdin, with `--force` required before overwriting an existing session.
- Keep session secrets minimal: only `config.json` and TDLib's `tdlib-db/td.binlog` are included; message history, media, images, thumbnails, downloads, exports, reports, attachment indexes, app binaries, SQLite cache databases, and lock files are excluded.

[Release v5.1.0](https://github.com/podkolzzzin/tgcli/releases/tag/v5.1.0)

## 5.0.0

- Fix channel `chat export --all-history` and `chat messages --all` pagination when TDLib returns short pages for channel-style message ids.
- Add channel export integrity evidence: public id range, missing public ids, fetched count, first/last post boundaries, and short-page warnings.
- Introduce the versioned `tgcli.message/5.0` JSONL schema with top-level `short_message_id`, `tg_url`, and flattened channel metrics: views, forwards, replies, reaction counts, paid reactions, and comment presence.
- Add `chat messages --schema rich` for JSON/JSONL output using the same rich message shape as `chat export`.
- Add `channel metrics` for per-post metrics plus JSON aggregates/CSV output.
- Add `channel comments` to resolve discussion threads and export the `channel_post_id -> discussion_chat_id -> discussion_message_id` mapping with comment rows, summaries, or inaccessible markers.
- Verify against `@DevJungles`: 567 accessible posts exported from public id range `1-575`, with 8 missing public ids reported.

[Release v5.0.0](https://github.com/podkolzzzin/tgcli/releases/tag/v5.0.0)

## 4.0.2

- Fix snake_case JSONL output for `chat messages` and `chat search`.
- Add a session attachment index so `download --attachment-id <file-id>` can resolve file ids seen by `message get`, message listings, and exports.
- Preserve `resume_token` in incremental manifests when no new rows are appended.
- Make `chat stats` return a stable object for attachment aggregates.
- Keep the launcher and release packaging fixes from 4.0.1.

[Release v4.0.2](https://github.com/podkolzzzin/tgcli/releases/tag/v4.0.2)

## 4.0.1

- Fix bundled TDLib native library resolution for installed single-file launchers.
- Send command errors to stderr and show concise user-facing messages instead of stdout stack traces.
- Align lightweight JSON/JSONL rows from chat/message listing and search commands with snake_case v4 field names.
- Treat missing extensionless `download --output` paths as directories to avoid accidental files named like directory targets.
- Document download output and session-lock behavior.

[Release v4.0.1](https://github.com/podkolzzzin/tgcli/releases/tag/v4.0.1)

## 4.0.0

- Introduce the versioned `tgcli.message/4.0` JSONL schema with stable sender identity, rich Telegram metadata, structured entities, complete attachment metadata, and resolved reply targets.
- Add chronological migration-aware exports and detailed completeness manifests.
- Add atomic incremental/resumable JSONL caches, resume tokens, field projections, and deletion tombstones.
- Add opt-in voice/video-note transcription hooks.
- Add machine-readable `diagnostics` and reply-aware `chat context`.
- Document TDLib, short-message, migration-source, reply, and permalink ID semantics.

The JSONL shape is intentionally incompatible with 3.x.
