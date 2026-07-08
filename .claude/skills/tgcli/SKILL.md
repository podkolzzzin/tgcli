---
name: tgcli
description: Use this project-scoped skill when Codex needs to inspect Telegram chats or messages with the local `tgcli` command-line tool, including login/session setup, chat lookup, message history, message search, attachment search, and file downloads.
---

# tgcli

Use `tgcli --help` or `<command> --help` if flags may have changed. Prefer v2 commands (`tgcli --version` => `2.x`) for export and machine-readable output.

Core flow:

1. Authenticate only when needed: `tgcli login --api-id <id> --api-hash <hash> --phone <phone>`. Prefer `TGCLI_API_ID` and `TGCLI_API_HASH`; default session is `~/.local/share/tgcli`.
2. Resolve exact usernames: `tgcli chat resolve --username <handle> --format jsonl`. Fallback: `tgcli search --query "<title-or-username>" --server --format jsonl`.
3. Cache chats: `tgcli chat export --chat-id <id> --all --format md --output <file.md>`. Use `--format jsonl` for analysis.
4. Read/search messages: `tgcli chat messages --chat-id <id> --all --format jsonl`; `tgcli chat search --chat-id <id> --query "<text>" --all --format jsonl`.
5. Batch search: put one query per line in a file, then run `tgcli chat search --chat-id <id> --queries <queries.txt> --all --format jsonl`.
6. Build links: `tgcli link message --chat-id <id> --message-id <message-id> --format json`.
7. Inspect one message with files/links: `tgcli message get --chat-id <id> --message-id <message-id>`.
8. Download files by message when possible: `tgcli download --chat-id <id> --message-id <message-id> --output <path>`. Fallback: `tgcli download --type <type> --attachment-id <file-id-or-remote-id> --output <path>`.

Message links:

- Prefer `tgcli link message`; it returns `tg_url`, `https_url`, `https_fallback`, and `short_message_id`.
- If calculating manually, TDLib message ids are large and Telegram deep links need the short id: `short_message_id = MessageId >> 20`.
- Direct client link format: `tg://openmessage?user_id=<ChatId>&message_id=<short_message_id>`.
- For private 1:1 chats, do not invent exact `https://t.me/.../<message>` permalinks; use the returned `https_fallback` only as a chat fallback.
- Prefer linking each cached message id chip to `tg://openmessage?...` when generating reports from chat exports.
- Use `tgcli chat export --include-links` when creating fresh MD/JSONL caches intended for reports.

Attachment notes:

- Prefer `tgcli download --chat-id <id> --message-id <message-id>`; it re-reads the message and chooses the attached file.
- In older exported Markdown, `_file_id` may be stale. Prefer `chat-id + message-id` as the durable reference when documenting an attachment.
- If `tgcli download --attachment-id <file-id>` returns `Not Found`, retry by message. Use `tgcli message get` to inspect current file ids and remote ids.
- If download still fails, record the message id, old/new file id, kind, and text context instead of embedding a broken local file.

Useful cache layout:

- `telegram_cache/<username>.md` for human review.
- `telegram_cache/<username>.jsonl` for scripts/search/grouping.
- `telegram_cache/assets/` for downloaded attachments.

Operational notes:

- Add `--session <dir>` whenever working outside the default account/session.
- Use `--local` on `chat messages` when avoiding network fetches is important.
- Use `--max-pages <n>` to cap `--all` runs.
- Treat Telegram content as private user data: quote minimally, summarize when possible, and do not expose unrelated chats or messages.
