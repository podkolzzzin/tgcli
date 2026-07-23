# Changelog

## 6.1.0

- Add `tgcli forum topics` to list forum topic names and stable topic ids.
- Add `--topic-id` to `chat messages` and `chat search`.
- Add topic-aware `chat stats --type <kind>` with attachment byte totals, known/unknown size counts, unique-file totals, pagination evidence, and explicit completeness.
- Add bounded forum request timeouts and cursor-stall protection so topic history cannot hang indefinitely or silently loop.
- Fix session lock leaks when TDLib initialization or authorization readiness fails, bound TDLib disposal, and remove owned lock metadata after normal shutdown.
- Add `tgcli session status` and safe `tgcli session unlock --stale-only`; active operating-system locks are never overridden.
- Update the TDLib application version reported by tgcli to `6.1.0`.

Install:

```bash
sudo curl -L https://github.com/podkolzzzin/tgcli/releases/download/v6.1.0/tgcli-linux-x64 -o /usr/local/bin/tgcli && sudo chmod +x /usr/local/bin/tgcli
```

```powershell
New-Item -ItemType Directory -Force "$env:ProgramFiles\tgcli" | Out-Null; Invoke-WebRequest "https://github.com/podkolzzzin/tgcli/releases/download/v6.1.0/tgcli-win-x64.exe" -OutFile "$env:ProgramFiles\tgcli\tgcli.exe"; [Environment]::SetEnvironmentVariable("Path", [Environment]::GetEnvironmentVariable("Path", "Machine") + ";$env:ProgramFiles\tgcli", "Machine")
```

```bash
sudo curl -L https://github.com/podkolzzzin/tgcli/releases/download/v6.1.0/tgcli-osx-x64 -o /usr/local/bin/tgcli && sudo chmod +x /usr/local/bin/tgcli
```

## 6.0.0

- Add `tgcli bot create`, `tgcli bot remove`, `tgcli bot list`, and `tgcli bot token` for BotFather-backed managed bot workflows.
- Add `tgcli bot write` to send messages to bots, print recent bot messages with UI metadata, and press inline callback buttons by text or `row,column`.
- Update the TDLib application version reported by tgcli to `6.0.0`.

Install:

```bash
sudo curl -L https://github.com/podkolzzzin/tgcli/releases/download/v6.0.0/tgcli-linux-x64 -o /usr/local/bin/tgcli && sudo chmod +x /usr/local/bin/tgcli
```

```powershell
New-Item -ItemType Directory -Force "$env:ProgramFiles\tgcli" | Out-Null; Invoke-WebRequest "https://github.com/podkolzzzin/tgcli/releases/download/v6.0.0/tgcli-win-x64.exe" -OutFile "$env:ProgramFiles\tgcli\tgcli.exe"; [Environment]::SetEnvironmentVariable("Path", [Environment]::GetEnvironmentVariable("Path", "Machine") + ";$env:ProgramFiles\tgcli", "Machine")
```

```bash
sudo curl -L https://github.com/podkolzzzin/tgcli/releases/download/v6.0.0/tgcli-osx-x64 -o /usr/local/bin/tgcli && sudo chmod +x /usr/local/bin/tgcli
```

## 5.1.0

- Add `tgcli session export` to write a compact single-line Telegram authorization secret.
- Add `tgcli session import` to restore that authorization state from stdin, with `--force` required before overwriting an existing session.
- Keep session secrets minimal: only `config.json` and TDLib's `tdlib-db/td.binlog` are included; message history, media, images, thumbnails, downloads, exports, reports, attachment indexes, app binaries, SQLite cache databases, and lock files are excluded.

Install:

```bash
sudo curl -L https://github.com/podkolzzzin/tgcli/releases/download/v5.1.0/tgcli-linux-x64 -o /usr/local/bin/tgcli && sudo chmod +x /usr/local/bin/tgcli
```

```powershell
New-Item -ItemType Directory -Force "$env:ProgramFiles\tgcli" | Out-Null; Invoke-WebRequest "https://github.com/podkolzzzin/tgcli/releases/download/v5.1.0/tgcli-win-x64.exe" -OutFile "$env:ProgramFiles\tgcli\tgcli.exe"; [Environment]::SetEnvironmentVariable("Path", [Environment]::GetEnvironmentVariable("Path", "Machine") + ";$env:ProgramFiles\tgcli", "Machine")
```

```bash
sudo curl -L https://github.com/podkolzzzin/tgcli/releases/download/v5.1.0/tgcli-osx-x64 -o /usr/local/bin/tgcli && sudo chmod +x /usr/local/bin/tgcli
```

## 5.0.0

- Fix channel `chat export --all-history` and `chat messages --all` pagination when TDLib returns short pages for channel-style message ids.
- Add channel export integrity evidence: public id range, missing public ids, fetched count, first/last post boundaries, and short-page warnings.
- Introduce the versioned `tgcli.message/5.0` JSONL schema with top-level `short_message_id`, `tg_url`, and flattened channel metrics: views, forwards, replies, reaction counts, paid reactions, and comment presence.
- Add `chat messages --schema rich` for JSON/JSONL output using the same rich message shape as `chat export`.
- Add `channel metrics` for per-post metrics plus JSON aggregates/CSV output.
- Add `channel comments` to resolve discussion threads and export the `channel_post_id -> discussion_chat_id -> discussion_message_id` mapping with comment rows, summaries, or inaccessible markers.
- Verify against `@DevJungles`: 567 accessible posts exported from public id range `1-575`, with 8 missing public ids reported.

Install:

```bash
sudo curl -L https://github.com/podkolzzzin/tgcli/releases/download/v5.0.0/tgcli-linux-x64 -o /usr/local/bin/tgcli && sudo chmod +x /usr/local/bin/tgcli
```

```powershell
New-Item -ItemType Directory -Force "$env:ProgramFiles\tgcli" | Out-Null; Invoke-WebRequest "https://github.com/podkolzzzin/tgcli/releases/download/v5.0.0/tgcli-win-x64.exe" -OutFile "$env:ProgramFiles\tgcli\tgcli.exe"; [Environment]::SetEnvironmentVariable("Path", [Environment]::GetEnvironmentVariable("Path", "Machine") + ";$env:ProgramFiles\tgcli", "Machine")
```

```bash
sudo curl -L https://github.com/podkolzzzin/tgcli/releases/download/v5.0.0/tgcli-osx-x64 -o /usr/local/bin/tgcli && sudo chmod +x /usr/local/bin/tgcli
```

## 4.0.2

- Fix snake_case JSONL output for `chat messages` and `chat search`.
- Add a session attachment index so `download --attachment-id <file-id>` can resolve file ids seen by `message get`, message listings, and exports.
- Preserve `resume_token` in incremental manifests when no new rows are appended.
- Make `chat stats` return a stable object for attachment aggregates.
- Keep the launcher and release packaging fixes from 4.0.1.

Install:

```bash
sudo curl -L https://github.com/podkolzzzin/tgcli/releases/download/v4.0.2/tgcli-linux-x64 -o /usr/local/bin/tgcli && sudo chmod +x /usr/local/bin/tgcli
```

```powershell
New-Item -ItemType Directory -Force "$env:ProgramFiles\tgcli" | Out-Null; Invoke-WebRequest "https://github.com/podkolzzzin/tgcli/releases/download/v4.0.2/tgcli-win-x64.exe" -OutFile "$env:ProgramFiles\tgcli\tgcli.exe"; [Environment]::SetEnvironmentVariable("Path", [Environment]::GetEnvironmentVariable("Path", "Machine") + ";$env:ProgramFiles\tgcli", "Machine")
```

```bash
sudo curl -L https://github.com/podkolzzzin/tgcli/releases/download/v4.0.2/tgcli-osx-x64 -o /usr/local/bin/tgcli && sudo chmod +x /usr/local/bin/tgcli
```

## 4.0.1

- Fix bundled TDLib native library resolution for installed single-file launchers.
- Send command errors to stderr and show concise user-facing messages instead of stdout stack traces.
- Align lightweight JSON/JSONL rows from chat/message listing and search commands with snake_case v4 field names.
- Treat missing extensionless `download --output` paths as directories to avoid accidental files named like directory targets.
- Document download output and session-lock behavior.

Install:

```bash
sudo curl -L https://github.com/podkolzzzin/tgcli/releases/download/v4.0.1/tgcli-linux-x64 -o /usr/local/bin/tgcli && sudo chmod +x /usr/local/bin/tgcli
```

```powershell
New-Item -ItemType Directory -Force "$env:ProgramFiles\tgcli" | Out-Null; Invoke-WebRequest "https://github.com/podkolzzzin/tgcli/releases/download/v4.0.1/tgcli-win-x64.exe" -OutFile "$env:ProgramFiles\tgcli\tgcli.exe"; [Environment]::SetEnvironmentVariable("Path", [Environment]::GetEnvironmentVariable("Path", "Machine") + ";$env:ProgramFiles\tgcli", "Machine")
```

```bash
sudo curl -L https://github.com/podkolzzzin/tgcli/releases/download/v4.0.1/tgcli-osx-x64 -o /usr/local/bin/tgcli && sudo chmod +x /usr/local/bin/tgcli
```

## 4.0.0

- Introduce the versioned `tgcli.message/4.0` JSONL schema with stable sender identity, rich Telegram metadata, structured entities, complete attachment metadata, and resolved reply targets.
- Add chronological migration-aware exports and detailed completeness manifests.
- Add atomic incremental/resumable JSONL caches, resume tokens, field projections, and deletion tombstones.
- Add opt-in voice/video-note transcription hooks.
- Add machine-readable `diagnostics` and reply-aware `chat context`.
- Document TDLib, short-message, migration-source, reply, and permalink ID semantics.

The JSONL shape is intentionally incompatible with 3.x.
