# Additional Codex session coverage and tgcli flow corroboration

- The local indexed Codex history projection contained 1,198 distinct threads and 187,100 items at scan time. Every projected thread matched a local native rollout UUID, including the compressed files. The 866 state-index paths without plain JSONL were not reliable indicators of missing sessions. Deduplication yielded zero additional threads from SQLite. The local native inventory had 1,225 distinct UUIDs during this check, including the active task’s changing counts of plain files, and 881 compressed rollouts.
- A targeted file-name search under readable local home and task-related paths found no additional plain or compressed Codex rollouts. The Codex backups directory contained a configuration backup. The root account's home directory was permission-restricted and no access was attempted beyond a path existence probe. The local active and archived rollouts and command-history sidecar are covered by the primary scan.
- The live device registry listed three devices. Existing strict host-key checked, batch-only SSH reached both peers. The MacBook had one active JSONL rollout with zero textual tgcli matches and no archived JSONL rollouts. A second probe for compressed files timed out after the MacBook's SSH endpoint became unreachable. Compressed MacBook rollouts therefore remain unverified. The work laptop had no readable standard Codex active or archive directory. No historical command was executed.
- Searching the SQLite projections of locally compressed rollouts corroborated 295 directly attributed tgcli MCP calls in 12 threads, with 278 recorded completed and 17 failed. Another 416 terminal records in 29 threads contained CLI-shaped commands. Those terminal candidates include help and incidental mentions. The MCP rows derive from the structured server field and represent calls attributed to tgcli. Counts overlap the primary archive scan.

## Observed MCP failures

| Count | Scenario | Release-flow assertion |
| --- | --- | --- |
| 7 | Resumed message history rejected the backend `from_message_id` after an earlier page armed a cursor. | Continue a real cursor across multiple pages at limits 50 and 100. Use the backend-specific ID, preserve order and avoid duplicate or skipped messages. |
| 3 | Chat list rejected limits above 100. | Advertise the bound and reject 101 before acquiring a session. |
| 2 | Chat discovery encountered another tgcli process holding the session. | A contending caller gets a bounded lock outcome, then succeeds after the owner exits. |
| 1 | Chat discovery timed out while native work was active. | The time budget produces a limited error and session reuse becomes possible after the native operation settles. |
| 1 | Attachment download rejected a 600-second request timeout. | The documented legal timeout works. An out-of-range timeout fails preflight without a file write. |
| 1 | Chat resolve was called with `chat` in place of required `username`. | Tool/schema discovery exposes `username`, with actionable missing-argument feedback. |
| 1 | Chat list exceeded the response cap at a 100-entry request. | Oversized response yields a bounded structured error. A smaller request returns a valid result. |
| 1 | Context validation rejected `before=100`. | Test the `before`/`after` boundaries and the assistant's ability to retry with legal arguments. |

## Additional flow assertions

- Chat discovery by title and public username. Exercise a local miss and server lookup. For bounded discovery, empty and nonempty results both reported `bounded_no_continuation`, with `complete=false`, `has_more=false` and no cursor.
- Read a chat page, follow its cursor, fetch an individual message and use its deep link with reply-chain context. Match the short post ID to the TDLib message ID and preserve reply targets. Historical calls include one filtered empty `chat_messages` page carrying a cursor and `has_more=true`, so a consumer must continue an empty nonterminal page.
- Search scoped messages and attachments, then fetch context and download the selected typed attachment. Message search had 31 nonterminal pages with cursors and 12 exhausted empty responses. Test one confirmed complete match set across the boundary and an observable absolute-path file write with metadata.
- Query forum topics, choose one topic, then read topic history and media via CLI. MCP query-filtered topic pages all carried continuation in this sample. Test that all cursor offsets survive and topic history stays scoped.
- Exercise standalone offline transcription and export transcription, including a language override, with generated media fixtures. Check long or cancelled input and deterministic export status, with no historical payloads in fixtures.
- Exercise MCP installation dry-run and a custom session selection. Verify the target app configuration is parseable and the plan does not change it. Use a staged configuration fixture for install behavior.
- Exercise batch download from a synthetic attachment manifest with bounded concurrency, per-item receipts and explicit single/batch attachment-kind matching. The historical terminal candidates include both single selected attachment and batch flow invocations.

Historical arguments, transcripts, native IDs and user-specific resource identifiers remain in the private scratch directory. The assertions above use synthetic inputs.
