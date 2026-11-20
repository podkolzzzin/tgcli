# tgcli

Small command-line tool for reading Telegram chats with TDLib.

## MCP

Version 8.0.0 updates the CLI and MCP read contracts, export snapshots, and release installation. See the [v8 migration guide](docs/v8-migration.md) before updating scripts or existing exports.

```bash
tgcli mcp
tgcli mcp install claude
tgcli mcp install codex
tgcli mcp install copilot
# Inspect proposed changes first, or select another Telegram account:
tgcli mcp install copilot --dry-run
tgcli mcp install claude --session /absolute/path/to/session
```

MCP `2026-07-28` uses per-request metadata without a handshake; older clients can still initialize with `2025-11-25`, `2025-06-18`, or `2024-11-05`. Twelve tools expose Telegram reading, search, context, statistics, links, diagnostics and attachment downloads. Login separately with `tgcli login` before using Telegram tools. An MCP process retains its session lock, so stop it before another client uses the same Telegram database.

See [MCP.md](MCP.md) for the client/OS matrix, detection rules, tools, configuration, verification, removal, and known limitations. The [implementation plan](MCP_PLAN.md) retains the outstanding acceptance checks; Desktop/IDE interoperability is not yet fully verified.

The shared CI workflow defines packaged CLI, installer, local transcription, and MCP checks on Linux x64, Intel macOS, and Windows x64 for pull requests, main, and releases. MCP checks use isolated profiles and simulated client launchers.

## Install

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

## Agent Skill

Install for Codex:

```bash
mkdir -p ~/.codex/skills/tgcli && curl -fsSL https://raw.githubusercontent.com/podkolzzzin/tgcli/main/.codex/skills/tgcli/SKILL.md -o ~/.codex/skills/tgcli/SKILL.md
```

Install for Claude:

```bash
mkdir -p ~/.claude/skills/tgcli && curl -fsSL https://raw.githubusercontent.com/podkolzzzin/tgcli/main/.claude/skills/tgcli/SKILL.md -o ~/.claude/skills/tgcli/SKILL.md
```

## Login

Create Telegram API credentials at <https://my.telegram.org/apps>, then run:

```bash
tgcli login --api-id 12345 --api-hash your_api_hash
```

You only need to login once. The local Telegram session is stored on your machine.

## Examples

```bash
tgcli chat list
tgcli search --query "some chat"
tgcli chat messages --chat-id 123456789
tgcli forum topics --chat-id 123456789 --all
tgcli chat messages --chat-id 123456789 --topic-id 10 --all
tgcli chat search --chat-id 123456789 --topic-id 10 --type video --all
tgcli chat stats --chat-id 123456789 --topic-id 10 --type video
tgcli chat export --chat-id 123456789 --format md --output chat.md
tgcli chat export --chat-id 123456789 --format jsonl --output chat.jsonl
tgcli channel metrics --chat-id 123456789 --format json
tgcli channel comments --chat-id 123456789 --post-id 42 --format jsonl
tgcli session export > tgcli.session
tgcli session import < tgcli.session
tgcli chat context --chat-id 123456789 --message-id 987654321 --before 10 --after 10
tgcli diagnostics --format json
tgcli download --chat-id 123456789 --message-id 987654321 --output ./files
tgcli download-batch --input attachments.jsonl --output ./files --parallel 4
tgcli bot list
tgcli bot create --name "Example Bot" --username example_unique_bot
tgcli bot token --username example_unique_bot
tgcli bot write --username example_unique_bot --text /start --format json
tgcli bot write --username example_unique_bot --click "0,0" --message-id 987654321 --format json
tgcli bot remove --username example_unique_bot --confirm
```

`download --output` accepts either a file or directory. Existing directories, paths ending with a path separator, and missing paths without an extension are treated as directories.

`download-batch` accepts JSONL with decimal string `chat_id` and `message_id` fields, including rows produced by filtered `chat search`, `chat messages`, or `chat export` commands. It downloads through one TDLib client with bounded concurrency and writes one result row per input row:

```bash
tgcli chat search --chat-id 123456789 --query "" --type video --all --format jsonl > videos.jsonl
tgcli download-batch --input videos.jsonl --type video --output ./videos --parallel 4
```

Use `--input -` to read JSONL from stdin. An input row can select an exact file with `attachment_id` or `file_id`. Output filenames distinguish attachments within a message. Multiple rows selecting the same attachment share a download and each receives a receipt in input order. Reusing the output directory lets a rerun skip files with verified attachment identity, length and SHA-256. A failed item produces an error receipt and exit code `3`.

## Export schema and completeness

Message records in JSON and JSONL have `schema` set to `tgcli.message` and `schema_version` set to `8.0`. They include source chat and message IDs, links, sender identity, reply and forwarding context, edits, metrics, polls, service events, and attachment metadata. JSON command output is one document. JSONL output contains one data record per line. Message lists sort chronologically by date, chat ID, and message ID.

`chat export` defaults to complete current history, including migrated history when present. Use `--limit N` with 1–100 for a bounded export. Check the exit code and the sibling `<output>.manifest.json` for the acquisition scope, coverage and termination reason. Use `--incremental` on a matching JSONL snapshot to reconcile edits and verified deletions from a complete refresh. For an older export schema, create a fresh v8 snapshot before refreshing. See the [migration guide](docs/v8-migration.md) for refresh and resume examples.

Use `--transcribe` to generate transcripts for voice notes, video notes, audio and video. Set `--transcribe-language uk` to select Ukrainian. Transcripts include the model, detected language and timestamped segments in `attachments[].generated_text`. Markdown, plain and TSV exports include a generated-transcription block. For an external engine, use `--transcribe-command <executable>` and optional `--transcribe-command-args` with a JSON array. tgcli passes those arguments followed by the absolute media path and reads the transcript from stdout. Choose either the external engine or `--transcribe`. See [offline media transcription](#offline-media-transcription) for the executable requirements.

`channel metrics` emits one row per post with views, forwards, replies, reactions, paid reactions, link domains, and engagement rate. `channel comments` resolves Telegram discussion threads. Comment rows include `channel_post_id`, `discussion_chat_id`, and `discussion_message_id`. JSONL keeps discussion coverage on stderr. JSON includes comments in `items` and coverage in `threads` and `post_scan`.

## Forum topics and attachment sizes

List a supergroup's forum topics, then use the returned `topic_id` with message, search, and stats commands:

```bash
tgcli forum topics --chat-id -1001234567890 --all --format jsonl
tgcli chat messages --chat-id -1001234567890 --topic-id 10 --all --format jsonl
tgcli chat search --chat-id -1001234567890 --topic-id 10 --type video --all --format jsonl
tgcli chat stats --chat-id -1001234567890 --topic-id 10 --type video --format json
```

Topic statistics include exact/estimated completeness, termination reason, pages fetched, attachment count, known/unknown size counts, total bytes, and unique-file totals. `--type video` excludes video notes; use `--type video-note` to count round video messages separately. Topic requests have a bounded `--request-timeout`, and pagination fails clearly if its cursor stops advancing.

## Bot management

`tgcli bot` exposes BotFather-backed bot management and bot chat interaction:

- `tgcli bot create --name "Name" --username name_bot` creates a managed bot.
- `tgcli bot list` prints bots owned by the current account.
- `tgcli bot token --username name_bot` prints a managed bot token; add `--revoke` to rotate it.
- `tgcli bot remove --username name_bot --confirm` starts BotFather's delete flow.
- `tgcli bot write --username name_bot --text /start --format json` sends text to a bot and returns recent messages with UI metadata.
- `tgcli bot write --username name_bot --click "Button text"` or `--click 0,1 --message-id <id>` presses inline callback buttons.

## Session secrets

`tgcli session export` writes a compact single-line secret for the current Telegram authorization state. `tgcli session import` reads that secret from stdin and restores the login on another machine.

```bash
tgcli session export > tgcli.session
tgcli session import < tgcli.session
```

For CI:

```bash
TGCLI_SESSION="$(tgcli session export)"
tgcli session import --session "$RUNNER_TEMP/tgcli-session" <<< "$TGCLI_SESSION"
```

The secret contains only `config.json` and TDLib's `tdlib-db/td.binlog` authorization state. It does not contain message history, media, images, documents, thumbnails, downloads, exports, reports, attachment indexes, app binaries, or lock files. Treat it like a logged-in Telegram session.

## ID semantics

- JSON and JSONL represent Telegram IDs, including topic and file IDs, as decimal strings. Counts, byte sizes, and Unix seconds are numbers. Timestamp strings and date-only selectors use UTC.
- `chat_id` is the TDLib chat containing the record. It remains the original source chat id across migrations.
- `message_id` is TDLib's 64-bit message id and is only unique within its chat.
- `short_message_id` is `message_id >> 20`, used in Telegram links; do not substitute it for `message_id` in API calls.
- `source_chat_id` identifies the fetched migration-chain segment.
- `reply_to_chat_id` must be paired with `reply_to_message_id`; replies can cross the fetched page or migration boundary.
- Exact HTTPS permalinks exist only where Telegram exposes a public/private-supergroup link. `tg_url` is the general client deep link.

## Read scope and exit codes

Discovery and reads sync with Telegram by default. Commands that accept `--local` read the cache, and collection coverage reports `source: "local_cache"`. Collection JSON includes rows in `items` alongside fields such as `complete`, `termination_reason` and any continuation. `chat search` groups results and coverage by query in `queries`. With JSONL or TSV, read acquisition coverage on stderr and check the exit code. For file exports, read the sibling manifest.

| Exit code | Meaning |
|---|---|
| 0 | Request fulfilled, including a bounded page or an authoritatively empty result |
| 1 | Operational failure |
| 2 | Invalid usage or input |
| 3 | Partial result |
| 4 | Log in or restore authorization |
| 5 | Request deadline exceeded |
| 130 | Cancelled |

`--request-timeout` accepts 1–3600 seconds and defaults to 60. It includes lock acquisition, readiness, and the operation itself. Where offered, `--lock-timeout` can shorten the lock wait and `--no-wait` tries to acquire the lock immediately. MCP tool calls have a separate deadline.

## Session storage

Keep your session directory private. It contains Telegram login data.

Session-backed commands use a lock file in the session directory, normally `~/.local/share/tgcli/tdlib.lock`. Close an active MCP process before using its database from the CLI.

Use `tgcli session status` to inspect a lock without opening Telegram. `tgcli session unlock --stale-only` removes stale owner metadata only when the operating-system lock is free; it refuses to override an active process. Failed initialization and readiness paths now dispose their sessions, TDLib shutdown is bounded, and normal shutdown removes its owner metadata.

This project is unofficial and is not affiliated with Telegram.


## Offline media transcription

```bash
# Local files need no Telegram login. Default JSON includes text and segments in seconds.
tgcli transcribe --input ./lecture.mp4 --language uk
tgcli transcribe --input ./recording.ogg --format plain

# Voice notes, video notes, full audio and full video use the same embedded engine.
tgcli message transcribe --chat-id <id> --message-id <tdlib-message-id> --language uk

tgcli chat export --chat-id <id> --format jsonl --output history.jsonl --transcribe
tgcli chat export --chat-id <id> --format md --output history.md --transcribe --transcribe-language uk
```

Transcription is opt-in and runs on the CPU in the tgcli process through Whisper.net. The release bundles the multilingual Whisper base weights (148 MB), native CPU runtime and FFmpeg decoder. The embedded engine runs with the bundled assets, including on a machine with no separate decoder, `setsid` utility or speech service. .NET extracts the bundled decoder/native libraries into the user's local tgcli transcription cache on first use and verifies checksums; model weights are loaded from the binary into memory. FFmpeg runs only to decode media, while model inference runs inside tgcli. Audio without speech may produce an empty transcript; a video without an audio track is an error. Recognition quality depends on the recording and the base model; generated transcripts are labeled accordingly.

On Linux, tgcli supervises an optional external transcriber in a process group so cancellation reaches children that stay in the group even after the launcher exits. On macOS and Windows, the executable must wait for its children before exiting. If an exited launcher leaves its output pipes open, tgcli reports an error. A detached child can outlive the command.

## Build from source

Use .NET 10 and Python 3.11 or later with build-time internet access. Run from the repository root:

```bash
python3 tools/build_release.py --rid linux-x64
```

Use `osx-x64` on Intel macOS or `py -3 tools/build_release.py --rid win-x64` on Windows x64. The script restores NuGet packages, prepares the pinned transcription assets, and publishes the same self-contained single file used by CI. Find the executable at `artifacts/<rid>/tgcli` or `artifacts/win-x64/tgcli.exe`.

Normal development builds can omit these assets. Publishing requires the embedded model and native libraries. See the [third-party notices](third_party/transcription/NOTICE.md) for component licenses and sources.
