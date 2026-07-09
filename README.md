# tgcli

Small command-line tool for reading Telegram chats with TDLib.

## Install

Linux:

```bash
sudo curl -L https://github.com/podkolzzzin/tgcli/releases/download/v4.0.0/tgcli-linux-x64 -o /usr/local/bin/tgcli && sudo chmod +x /usr/local/bin/tgcli
```

Windows PowerShell, as Administrator:

```powershell
New-Item -ItemType Directory -Force "$env:ProgramFiles\tgcli" | Out-Null; Invoke-WebRequest "https://github.com/podkolzzzin/tgcli/releases/download/v4.0.0/tgcli-win-x64.exe" -OutFile "$env:ProgramFiles\tgcli\tgcli.exe"; [Environment]::SetEnvironmentVariable("Path", [Environment]::GetEnvironmentVariable("Path", "Machine") + ";$env:ProgramFiles\tgcli", "Machine")
```

macOS:

```bash
sudo curl -L https://github.com/podkolzzzin/tgcli/releases/download/v4.0.0/tgcli-osx-x64 -o /usr/local/bin/tgcli && sudo chmod +x /usr/local/bin/tgcli
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
tgcli chat export --chat-id 123456789 --format md --output chat.md
tgcli chat export --chat-id 123456789 --all-history --format jsonl --output chat.jsonl --fail-incomplete
tgcli chat context --chat-id 123456789 --message-id 987654321 --before 10 --after 10
tgcli diagnostics --format json
tgcli download --chat-id 123456789 --message-id 987654321 --output ./files
```

## Export schema and completeness

JSONL exports use the versioned `tgcli.message/4.0` schema. Each record includes stable sender ids and separate display-name/username fields; source chat and message ids; resolved reply chat/message ids and a target preview; edit time; forwarding origin; reactions; polls; structured entities and service events; and complete attachment metadata. Telegram history does not return deleted message bodies. A complete `--incremental` refresh emits explicit tombstones for records that disappeared from the current history.

`--all-history` follows basic-group to supergroup migrations and produces one chronological, deduplicated stream. A sibling `<output>.manifest.json` records source chats, boundaries, per-chat and total page counts, retries, gaps, inaccessible messages, duplicates, termination reason, completeness, counts, and a resume token. Fresh writes and manifest updates are atomic.

Use `--fields chat_id,message_id,text` for a stable projection. Use `--since-message-id`, `--since-date`, or `--resume-token` to limit a later export. `--resume` deduplicates an existing JSONL cache; `--incremental` also emits deletion tombstones after a complete refresh. `--transcribe-command <executable>` passes each downloaded voice/video-note path as the final argument and labels stdout as generated transcription text.

## ID semantics

- `chat_id` is the TDLib chat containing the record. It remains the original source chat id across migrations.
- `message_id` is TDLib's 64-bit message id and is only unique within its chat.
- `short_message_id` is `message_id >> 20`, used in Telegram links; do not substitute it for `message_id` in API calls.
- `source_chat_id` identifies the fetched migration-chain segment.
- `reply_to_chat_id` must be paired with `reply_to_message_id`; replies can cross the fetched page or migration boundary.
- Exact HTTPS permalinks exist only where Telegram exposes a public/private-supergroup link. `tg_url` is the general client deep link.

## Notes

Keep your session directory private. It contains Telegram login data.

This project is unofficial and is not affiliated with Telegram.
