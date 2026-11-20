# Migrate tgcli 7.x automation to 8.0

Use the [verified installer](../README.md#install) to replace the executable at the path your scripts and MCP clients use. Run `tgcli --version` and confirm `8.0.0`. Close any process holding the Telegram session before updating. Existing sessions can be used after the update.

## Reads and request scope

- Discovery and message reads sync with Telegram by default. Remove `search --server`. Pass `--local` to supported commands when you intentionally need cache-only access. The result identifies `local_cache` as its source. Cached results cannot establish whether a chat or message is absent from Telegram.
- Keep `--limit N` for bounded reads. A request can return successfully after getting N rows while more exist. Use `--all` on commands that offer it when you need exhaustive results within the filters. If `chat messages` or `chat search` returns a `next_message_id`, pass it as `--from-message-id` with the same filters and an offset of zero. For batched searches, use the continuation from the specific query.
- Read the exit code and acquisition metadata. A zero-row result from exhausted remote history differs from a partial or cache-only read. For scripts consuming JSONL, keep stderr separate from stdout. Stderr carries the acquisition summary when relevant. Export to a file for a separate manifest.
- Use `--request-timeout <seconds>` to change the common 60-second budget for session lock wait, connection readiness, and the operation. `--lock-timeout` can shorten lock wait within that budget.

## JSON consumers

- Remove `--schema simple` or `--schema rich`. Message records in JSON and JSONL use `schema: "tgcli.message"` with `schema_version: "8.0"`.
- Read collection rows from `items` and check `complete`, `termination_reason` and any continuation. `chat search --queries <file> --format json` emits one document with per-query rows and coverage in `queries`. JSONL writes rows on stdout and applicable coverage on stderr.
- Use decimal strings for Telegram IDs, including chat, message, user, topic and file IDs. Keep IDs as strings to preserve precision. Counts, byte sizes and Unix seconds remain numbers. Parse timestamp strings as UTC. Date-only boundaries also use UTC.
- Message lists sort chronologically by date, chat ID and message ID. Query batches keep their labels. For links, pair `chat_id` with the TDLib `message_id`. Use the short Telegram post ID for `channel comments --post-id`.

## Export snapshots

Start a fresh snapshot in a new file when upgrading a 7.x export. Keep the data and the sibling `.manifest.json` together. A later refresh validates the schema, account, and export scope before changing that pair.

```bash
tgcli chat export --chat-id -1001234567890 --format jsonl --output history-v8.jsonl
```

`chat export` fetches complete current history by default. Remove `--all`, `--all-history`, and `--fail-incomplete` from export commands. To fetch a bounded sample, use `--limit N` and a separate output path. Use a separate path for a `--local` export. A cached export cannot update an existing snapshot or generate transcripts.

```bash
tgcli chat export --chat-id -1001234567890 --format jsonl --output history-v8.jsonl --incremental
tgcli chat export --chat-id -1001234567890 --format jsonl --output sample.jsonl --limit 20
```

Use `--incremental` to reconcile edits and deletions against the complete export scope. Use `--resume` to upsert history while retaining previously exported rows that are absent from the current acquisition. For a materialized view, read the complete current snapshot after success and keep the previous one if you need a change log.

## Batch downloads and transcription

Keep the same output directory to resume a batch. Files with a verified attachment identity, size and SHA-256 can be skipped. Repeated selectors share acquisition when they resolve to the same attachment. JSONL receipts preserve input order. Use `attachment_id` or `file_id` to select an exact file when a message has multiple attachments.

The embedded offline transcriber is still available. If you use `--transcribe-command`, pass its executable path and put separate arguments in a JSON array with `--transcribe-command-args`. On macOS and Windows, use an executable that waits for its child processes. Linux cancellation covers children that stay in the supervised process group.

## MCP clients

Refresh the tool catalog to pick up schema version 8.0. Send decimal strings for `topic_id` and `file_id`. `chat_search` defaults to online. Set `server: false` for cache-only calls. Follow opaque `next_cursor` values with unchanged filters. Check `healthy` and `error_code` in `diagnostics` and preserve structured rows from partial results. See the [MCP guide](../MCP.md#tools-and-result-contracts).

## Error handling

Treat exit codes `2` (invalid input), `3` (partial), `4` (authorization), `5` (deadline), and `130` (cancellation) separately from `1` (operational failure). Diagnostics can emit an unhealthy JSON report on stdout and a failure on stderr, with exit `4` when login is required. Confirm the result before retrying a bot action whose outcome is uncertain.
