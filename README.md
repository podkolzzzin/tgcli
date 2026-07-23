# tgcli

Small command-line tool for reading Telegram chats with TDLib.

## Install

Linux:

```bash
sudo curl -L https://github.com/podkolzzzin/tgcli/releases/download/v6.1.0/tgcli-linux-x64 -o /usr/local/bin/tgcli && sudo chmod +x /usr/local/bin/tgcli
```

Windows PowerShell, as Administrator:

```powershell
New-Item -ItemType Directory -Force "$env:ProgramFiles\tgcli" | Out-Null; Invoke-WebRequest "https://github.com/podkolzzzin/tgcli/releases/download/v6.1.0/tgcli-win-x64.exe" -OutFile "$env:ProgramFiles\tgcli\tgcli.exe"; [Environment]::SetEnvironmentVariable("Path", [Environment]::GetEnvironmentVariable("Path", "Machine") + ";$env:ProgramFiles\tgcli", "Machine")
```

macOS:

```bash
sudo curl -L https://github.com/podkolzzzin/tgcli/releases/download/v6.1.0/tgcli-osx-x64 -o /usr/local/bin/tgcli && sudo chmod +x /usr/local/bin/tgcli
```

Then open a new terminal and run:

```bash
tgcli --help
```

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
tgcli search "some chat"
tgcli chat messages --chat-id 123456789
tgcli forum topics --chat-id 123456789 --all
tgcli chat messages --chat-id 123456789 --topic-id 10 --all
tgcli chat search --chat-id 123456789 --topic-id 10 --type video --all
tgcli chat stats --chat-id 123456789 --topic-id 10 --type video
tgcli chat export --chat-id 123456789 --format md --output chat.md
tgcli chat export --chat-id 123456789 --all-history --format jsonl --output chat.jsonl --fail-incomplete
tgcli channel metrics --chat-id 123456789 --format json
tgcli channel comments --chat-id 123456789 --post-id 42 --format jsonl
tgcli session export > tgcli.session
tgcli session import < tgcli.session
tgcli chat context --chat-id 123456789 --message-id 987654321 --before 10 --after 10
tgcli diagnostics --format json
tgcli download --chat-id 123456789 --message-id 987654321 --output ./files
tgcli bot list
tgcli bot create --name "Example Bot" --username example_unique_bot
tgcli bot token --username example_unique_bot
tgcli bot write --username example_unique_bot --text /start --format json
tgcli bot write --username example_unique_bot --click "0,0" --message-id 987654321 --format json
tgcli bot remove --username example_unique_bot --confirm
```

`download --output` accepts either a file or directory. Existing directories, paths ending with a path separator, and missing paths without an extension are treated as directories.

## Export schema and completeness

JSONL exports use the versioned `tgcli.message/5.0` schema. Each record includes stable sender ids and separate display-name/username fields; source chat and message ids; top-level `short_message_id` and `tg_url`; resolved reply chat/message ids and a target preview; edit time; forwarding origin; channel metrics; polls; structured entities and service events; and complete attachment metadata. Telegram history does not return deleted message bodies. A complete `--incremental` refresh emits explicit tombstones for records that disappeared from the current history.

`--all-history` follows basic-group to supergroup migrations and produces one chronological, deduplicated stream. For channels, it also probes older public post ids when TDLib returns short history pages. A sibling `<output>.manifest.json` records source chats, boundaries, public id range, missing public ids, per-chat and total page counts, retries, gaps, inaccessible messages, duplicates, termination reason, completeness, counts, and a resume token. Fresh writes and manifest updates are atomic.

Use `--fields chat_id,message_id,text` for a stable projection. Use `--since-message-id`, `--since-date`, or `--resume-token` to limit a later export. `--resume` deduplicates an existing JSONL cache; `--incremental` also emits deletion tombstones after a complete refresh. `--transcribe-command <executable>` passes each downloaded voice/video-note path as the final argument and labels stdout as generated transcription text.

`channel metrics` emits one row per post with views, forwards, replies, reactions, paid reactions, link domains, and engagement rate. `channel comments` resolves Telegram discussion threads and includes `channel_post_id`, `discussion_chat_id`, and `discussion_message_id` for each exported row.

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

- `chat_id` is the TDLib chat containing the record. It remains the original source chat id across migrations.
- `message_id` is TDLib's 64-bit message id and is only unique within its chat.
- `short_message_id` is `message_id >> 20`, used in Telegram links; do not substitute it for `message_id` in API calls.
- `source_chat_id` identifies the fetched migration-chain segment.
- `reply_to_chat_id` must be paired with `reply_to_message_id`; replies can cross the fetched page or migration boundary.
- Exact HTTPS permalinks exist only where Telegram exposes a public/private-supergroup link. `tg_url` is the general client deep link.

## Notes

Keep your session directory private. It contains Telegram login data.

Session-backed commands use a lock file in the session directory, normally `~/.local/share/tgcli/tdlib.lock`. If another process owns the TDLib database, use `--lock-timeout <seconds>` to wait or `--no-wait` to fail immediately.

Use `tgcli session status` to inspect a lock without opening Telegram. `tgcli session unlock --stale-only` removes stale owner metadata only when the operating-system lock is free; it refuses to override an active process. Failed initialization and readiness paths now dispose their sessions, TDLib shutdown is bounded, and normal shutdown removes its owner metadata.

This project is unofficial and is not affiliated with Telegram.
