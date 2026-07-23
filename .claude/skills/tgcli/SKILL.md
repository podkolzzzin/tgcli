---
name: tgcli
description: Use this project-scoped skill when Claude needs to inspect Telegram chats or messages with the local `tgcli` command-line tool, including login/session setup, forum topics, chat lookup, message history, message search, attachment search, and file downloads.
---

# tgcli

Use `tgcli --help` or `<command> --help` if flags may have changed. Prefer v6 commands (`tgcli --version` => `6.x`) for forum-topic history and attachment totals, bot workflows, the versioned rich export schema, channel-aware pagination, integrity manifests, channel metrics/comments, incremental caches, diagnostics, and conversation context.

Core flow:

1. Authenticate only when needed: `tgcli login --api-id <id> --api-hash <hash> --phone <phone>`. Prefer `TGCLI_API_ID` and `TGCLI_API_HASH`; default session is `~/.local/share/tgcli`.
2. Resolve exact usernames: `tgcli chat resolve --username <handle> --format jsonl`. Fallback: `tgcli search --query "<title-or-username>" --server --format jsonl`.
3. Inspect migrations when history may span a basic-group -> supergroup upgrade: `tgcli chat migrations --chat-id <id> --format json`.
4. Cache chats with full migrated history: `tgcli chat export --chat-id <id> --all-history --format md --output <file.md> --include-links --fail-incomplete`. Use `--format jsonl` for analysis.
5. Read/search messages: `tgcli chat messages --chat-id <id> --all --format jsonl`; use `--schema rich` when message listings must match export JSONL rows. Search with `tgcli chat search --chat-id <id> --query "<text>" --all --format jsonl`.
6. Batch search: put one query per line in a file, then run `tgcli chat search --chat-id <id> --queries <queries.txt> --all --format jsonl`.
7. For channels, inspect metrics with `tgcli channel metrics --chat-id <id> --format json` and comments/discussion mapping with `tgcli channel comments --chat-id <id> --post-id <short-id> --format jsonl`.
8. Build links: `tgcli link message --chat-id <id> --message-id <message-id> --format json`.
9. Inspect one message with files/links: `tgcli message get --chat-id <id> --message-id <message-id> --format json`.
10. Export/import only Telegram authorization state with `tgcli session export > tgcli.session` and `tgcli session import < tgcli.session`.
11. Download files by message when possible: `tgcli download --chat-id <id> --message-id <message-id> --output <path>`. Fallback: `tgcli download --type <type> --attachment-id <file-id-or-remote-id> --output <path>`.
12. For forum supergroups, list topics with `tgcli forum topics --chat-id <id> --all --format jsonl`, then pass `--topic-id <id>` to `chat messages`, `chat search`, or `chat stats`.

Message links:

- Prefer `tgcli link message`; it returns `tg_url`, `https_url`, `https_fallback`, and `short_message_id`.
- If calculating manually, TDLib message ids are large and Telegram deep links need the short id: `short_message_id = MessageId >> 20`.
- Direct client link format: `tg://openmessage?user_id=<ChatId>&message_id=<short_message_id>`.
- For private 1:1 chats, do not invent exact `https://t.me/.../<message>` permalinks; use the returned `https_fallback` only as a chat fallback.
- Prefer linking each cached message id chip to `tg://openmessage?...` when generating reports from chat exports.
- Use `tgcli chat export --include-links` when creating fresh MD/JSONL caches intended for reports.

Export integrity and caching:

- `chat export --all-history` follows basic-group -> supergroup migrations automatically, preserves original `chat_id`/`message_id`, and deduplicates messages. For channels, it probes older public post ids when TDLib returns short pages.
- Export writes an integrity manifest to stderr and `<output>.manifest.json` with count, date boundaries, source chats, pages fetched, duplicate count, warnings, completeness, and for channels the public id range, missing public ids, fetched count, first post, and last post.
- Use `--expect-since <date>`, `--expect-count-min <n>`, and `--fail-incomplete` when the export must meet known coverage requirements.
- Use `--resume` or `--incremental` only with existing JSONL caches; tgcli validates cached JSONL before appending and uses atomic writes for fresh exports.
- JSONL uses schema `tgcli.message/5.0`; use `--fields` for stable projections, `--since-message-id`, `--since-date`, or `--resume-token` for incremental runs, and `--transcribe-command` for opt-in voice/video-note transcription. v5 rows include top-level `short_message_id`, `tg_url`, `view_count`, `forward_count`, `reply_count`, `reaction_counts`, `paid_reaction_count`, and `has_comments`.
- `tgcli diagnostics --format json` checks the session, TDLib version, authenticated account, local database, and network.
- `tgcli chat context --chat-id <id> --message-id <id> --before 5 --after 5` returns a validation window and reply chain.

Channel analytics:

- `tgcli channel metrics --chat-id <id> --format jsonl|json|csv` emits per-post metrics, link domains, and engagement rate; JSON output includes aggregates and top posts by views.
- `tgcli channel comments --chat-id <id> --post-id <short_message_id> --format jsonl|json` resolves the linked discussion thread and preserves `channel_post_id -> discussion_chat_id -> discussion_message_id`.
- Add `--summary` to `channel comments` for per-post comment count, unique commenters, top commenters, first/last comment date, and inaccessible markers.

Session secrets:

- `tgcli session export` writes a compact single-line secret for the current Telegram authorization state.
- `tgcli session import` reads that secret from stdin; add `--force` only when replacing an existing session directory.
- The secret contains only `config.json` and `tdlib-db/td.binlog`. It does not contain message history, media, images, thumbnails, downloads, exports, reports, attachment indexes, app binaries, SQLite cache databases, or lock files.

Service messages and stats:

- Search service messages with `tgcli chat messages --chat-id <id> --all --service-only --format jsonl`; add `--kind chat-upgrade-from` or similar normalized kinds to narrow results.
- Use `tgcli chat stats --chat-id <id> --format json` for fast boundaries/counts, attachment totals, participant count when available, and migration summary.
- Use `tgcli chat stats --chat-id <id> --topic-id <id> --type video --format json` for exact forum-topic video counts and byte totals. Video notes are separate under `--type video-note`.

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
- If another tgcli process owns the TDLib database, use `--lock-timeout <seconds>` to wait or `--no-wait` to fail immediately with owner PID diagnostics.
- Inspect locks with `tgcli session status`; repair owner metadata only with `tgcli session unlock --stale-only`, which refuses active OS locks.
- Keep progress and manifests on stderr; parse stdout only for requested `json`, `jsonl`, `tsv`, or `plain` output.
- Treat Telegram content as private user data: quote minimally, summarize when possible, and do not expose unrelated chats or messages.
