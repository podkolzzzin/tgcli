# Telegram reading audit

Audit date: 2026-11-20. Scope: current dirty checkout at `/home/andrii/RiderProjects/tgcli`; no tracked files edited, no live Telegram session, no messages sent. Audited `TgCommands.cs`, `TelegramOperations.cs`, `ForumTopics.cs`, `MessageLinks.cs` and their dependencies/tests. Root agent owns whole-suite execution; this sub-audit ran only a synthetic standalone probe against the current compiled Debug assembly.

Evidence classifications: **runtime confirmed** means a pure helper/real TDLib model was exercised without a Telegram session; **source/contract confirmed** means a deterministic branch or official TDLib contract mismatch; **integration question** means actual account/client behavior remains untested. A contract defect is not a claim that it was observed against the user's account.

## Reproduction artifacts

- `/tmp/tgcli-read-probe/ReadProbe.csproj`: standalone net10.0 project, `AssemblyName=TgCli.Tests` to access existing internals, direct references to the checkout's `bin/Debug/net10.0/tgcli.dll`, `TdLib.dll`, `TdLib.Api.dll`, `Newtonsoft.Json.dll`.
- `/tmp/tgcli-read-probe/Program.cs`: synthetic inputs only; no native client initialization.
- `/tmp/tgcli-read-probe/output.txt`: actual output from `dotnet run --project /tmp/tgcli-read-probe/ReadProbe.csproj --verbosity quiet` (exit 0):

```text
forum_short_page: complete=True, reason=short_page, count=1, requests=0, unused_pages=2
forum_bounded: complete=True, reason=page_complete, count=1
migration_basic_has_upgrade=True, full_info_has_upgrade=False
migration_reflection_full_info_value=null
service_only_accepts_non_service_ids=1,2,3
local_cache_exhausted: complete=True, reason=history_exhausted, count=1
metric_aggregate_exception=OverflowException
group_link=tg://openmessage?user_id=-1001234567890&message_id=42
```

The first temporary project build lacked `TdLib.Api.dll`; that dependency was added only to the temporary project, then the probe ran successfully. No repository tests were changed.

## Coverage matrix

| Surface | Inspected implementation | Ordering / limits / freshness | Coverage and outcome |
|---|---|---|---|
| `chat list` | `TgCommands.cs:917`; `TelegramOperations.cs:9` | Main list only; clamp 1..1000; single GetChats; no continuation | No direct tests. Informational snapshot, not an all-chats enumeration; readiness does not prove complete chat-list refresh. CLI inventory agent separately found accepted JSON/plain formats silently print nothing. |
| Top-level chat `search` | `TgCommands.cs:82`; `TelegramOperations.cs:12` | Clamp 1..100; local known-chat search or server known-chat search, not a union | No direct tests. `--server` help incorrectly promises public-chat discovery. |
| `chat resolve` | `TelegramOperations.cs:15` | Trims whitespace/@ and calls SearchPublicChat | No direct tests. Blank rejection implemented; does not parse t.me URLs (not promised). |
| `chat messages`, one page | `TgCommands.cs:951,1010` | TDLib IDs, supplied cursor/offset, limit <=100; fewer than requested allowed | No direct facade tests. Filtering occurs after retrieval; no continuation metadata in CLI. `--from-message-id` help says before, API offset 0 includes that message. |
| `chat messages --all` | `TgCommands.cs:993`; `ExportSupport.cs:31` | Newest start, 100/page, sort ascending in helper; simple/rich renderers may differ | ChatHistoryTests cover short private pages, stalls, budget, bounded/empty results. Supplied from-message-id and offset are silently ignored; request-timeout and cancellation also bypassed on this branch. |
| Forum history/messages | `ForumTopics.cs:61`; `TgCommands.cs:980` | Descending IDs, dedupe, explicit stall/budget status, per-request timeout | Three ExportTests, but one asserts short-page exactness incorrectly. Probe reproduces false Complete on short and bounded pages. |
| Message `search` / query files | `TgCommands.cs:1076,1345` | Queries trimmed/deduped ignoring case; page-local type filtering; --all uses fixed100 | No CLI paging test. Short-page early EOF; ignores authoritative NextFromMessageId; offset ignored in --all; max budget silently truncates. MCP continuation test is separate and correct. |
| `chat context` | `TelegramOperations.cs:78`; `TgCommands.cs:1269` | One window <=100 plus <=100 ancestor fetches; no final sort; before/after not fully validated | No direct tests. Short window not filled; target fetched for reply walking but not explicitly added; ancestor lookup errors silently terminate chain. CLI `after=100` creates invalid TDLib offset -100. |
| `message get` | `TgCommands.cs:487` | Native full message plus links/sender/files; attachment index side effect | No command-level tests. Core read errors propagate; optional sender/link lookup errors swallowed. No refresh/version guarantee beyond TDLib response. TSV sender name is not cleaned. |
| `link message` | `MessageLinks.cs:20,25`; `TgCommands.cs:418` | Generated tg link; GetMessageLink authoritative HTTPS; optional username fallback | No link tests. Runtime proves group IDs put in user_id; cross-client compatibility needs integration check. Fallback is chat URL, not exact message. |
| `forum topics` | `ForumTopics.cs:222` | Three-part server cursor and dedupe; limit1..100, all page budget | No tests. Stops on short page despite next offsets; silently stops at max-pages without status. |
| `chat migrations` | `TgCommands.cs:1039`; `ExportSupport.cs:202` | Intended oldest→newest, cycle guard | No migration test. Reflection confirms wrong model used for forward upgrade metadata; legacy start misses supergroup. |
| `chat stats` | `TelegramOperations.cs:22` | Full history incl migrations; estimated/exact flag; timestamps; attachment counts | Attachment helper tests exist. Inherits forum false exactness, migration omission, and local cache EOF exactness; whole-chat request-timeout unused. Participant count only attempted for supergroups. |
| `channel metrics` | `TgCommands.cs:605,714,740` | Entire history fetched then post/date filters; order by post_id; inclusive endpoints | Metrics serialization test only. Discards history completeness. Int aggregate sums overflow. Link domains only plain-text HTTP(S), not entity URLs. Dates parse after expensive history read. |
| `channel comments` / summary | `TgCommands.cs:670,755,847` | Thread starter messages first; backward pagination; per-page sort only; per-post max-pages | No tests. Cursor starts from oldest root, skipping newer comments; short-page EOF and no progress check; summaries expose observed counts without completeness. |

## Confirmed defects

### R1 — P1: forum reads incorrectly declare complete on a short page

**Evidence:** runtime confirmed and TDLib contract confirmed. `ForumTopics.cs:112-117` sets `Complete=true` whenever response size < pageLimit. Synthetic pages [100], [90,80], [] return only [100], Complete=true, leaving two unread pages. A one-page bounded fetch also returns Complete=true with reason page_complete.

**Expected:** a short page is not proof of EOF; all-mode must continue until exhaustion/proven completion or report incomplete. **Actual:** `chat messages --topic-id ... --all` emits incomplete history without triggering its incomplete check; `chat stats --topic-id ...` labels count exact (`TelegramOperations.cs:55-65`). **Root cause:** treating a performance-dependent response size as EOF.

**Tests:** `TgCli.Tests/ExportTests.cs:220-261` currently ratifies this error (`Assert.True(result.Complete)`, `short_page`). Add short-page-followed-by-more, short first page at budget, bounded full page, and inclusive-anchor terminal cases. Official contract explicitly allows fewer messages than limit: [getForumTopicHistory](https://core.telegram.org/tdlib/docs/classtd_1_1td__api_1_1get_forum_topic_history.html).

### R2 — P1: channel comments pagination starts at the thread root and omits replies

**Evidence:** source/contract confirmed; no live account run. `TgCommands.cs:782` adds `thread.Messages`, then line783 sets cursor to their minimum ID, and lines786-792 request history from that cursor with offset0. TDLib defines `MessageThreadInfo.Messages` as thread starter messages, not a latest-comment page.

**Concrete repro:** thread root ID100, comments ID110/120, GetMessageThread returns starter [100]. Expected history first request `FromMessageId=0`, retrieving120/110/root; actual first request is100 and returns root/older portion, excluding110/120. Summary can report comment_count0 for an active discussion. Root messages from albums also require classification, not assuming only one ID is a root.

**Other defects in same loop:** short-page break at801 repeats R1; no cursor advancement check, so a full repeated page runs to max-pages; budget exhaustion has no completeness status; rows are sorted within each page but appended newest page before older page. Missing tests: root with later replies, multi-root album, short pages, repeated full page, budget truncation, thread inaccessible versus transient error. Contracts: [messageThreadInfo](https://core.telegram.org/tdlib/docs/classtd_1_1td__api_1_1message_thread_info.html), [getMessageThreadHistory](https://core.telegram.org/tdlib/docs/classtd_1_1td__api_1_1get_message_thread_history.html).

### R3 — P1: CLI search disregards authoritative continuation and truncates silently

**Evidence:** source/contract confirmed. `TgCommands.cs:1386-1396` derives cursor from minimum ID and stops on <100 results, never reading `FoundChatMessages.NextFromMessageId`. If a response contains one result and nonzero next cursor, `--all` returns one result. An empty page with a nonzero continuation also stops. Exhausting max-pages returns the collected List as if successful (`1400`) without warning/metadata. In all mode caller offset is omitted (`1107-1117`).

**Expected:** follow TDLib next cursor verbatim; stop on zero; expose budget/stall status. **Root cause:** a second legacy pagination loop instead of the continuation-aware logic already used by MCP (`McpTelegramBackend.cs:62-78`). Existing test `McpToolsTests.cs:99-115` proves MCP continuation behavior but provides no coverage of CLI search. Add divergent next/min-ID, empty continuation, short continuation, and max-pages tests. Contracts: [foundChatMessages](https://core.telegram.org/tdlib/docs/classtd_1_1td__api_1_1found_chat_messages.html), [searchChatMessages](https://core.telegram.org/tdlib/docs/classtd_1_1td__api_1_1search_chat_messages.html).

### R4 — P1: migration traversal from legacy basic group never discovers its upgrade

**Evidence:** runtime model reflection confirmed; path source confirmed. `ExportSupport.cs:208-209` gets BasicGroupFullInfo then reflects `UpgradedToSupergroupId`; that property exists on BasicGroup, not BasicGroupFullInfo. Probe prints true/false/null for exactly these types/access.

**Repro:** call migrations/stats/export with ID of a basic group that was upgraded. Expected legacy and new supergroup in chain; actual forward branch cannot run, and backward walk terminates on basic group (`227-230`). Inherited effects: stats/export omit the new group's messages while seeming to follow migrations. Starting with new supergroup can still follow backward correctly. Missing test: legacy-start and new-start give same chain, including normal non-migrated basic group. [basicGroup contract](https://core.telegram.org/tdlib/docs/classtd_1_1td__api_1_1basic_group.html), [basicGroupFullInfo contract](https://core.telegram.org/tdlib/docs/classtd_1_1td__api_1_1basic_group_full_info.html).

### R5 — P1: whole-chat `--all` silently ignores the requested start cursor

**Evidence:** source confirmed. `TgCommands.cs:993-995` passes no `fromMessageId`/offset to ChatHistory.FetchAsync; `ExportSupport.cs:52` always initializes cursor0. `chat messages --chat-id X --all --from-message-id Y` retrieves newer messages too; topic mode honors the cursor. `--offset` likewise ignored for whole-chat all mode.

**Expected:** apply requested start/offset or reject incompatible combination. **Actual:** output scope differs from the explicit CLI inputs. No command-level regression test. A now-unused `FetchHistoryAsync` at `TgCommands.cs:1313` still accepts cursor, suggesting refactor drift.

### R6 — P1/P2: metrics and topic listing silently discard truncation information

**Evidence:** source confirmed. Metrics receives `HistoryFetchResult` at `TgCommands.cs:617`, but reads only Messages. For `--max-pages1` on a channel with >100 posts, JSON emits aggregates/post_count for latest fetched subset with no Complete/termination status. An older requested date range may be empty solely because retrieval stopped early. `channel comments --all` similarly drops the post-list history status at685-687. `forum topics --all` stops on short pages at `ForumTopics.cs:277` and returns without any budget status after loop292.

**Expected:** retained completeness and cursor/status, or explicit failure. **Actual:** partial results resemble full exports. Topic short-page EOF violates the [getForumTopics contract](https://core.telegram.org/tdlib/docs/classtd_1_1td__api_1_1get_forum_topics.html). Missing tests cover full page at cap, short topic page with nonzero next offsets, and filtered range outside the retrieved subset.

### R7 — P2: `service_only` includes ordinary media and polls

**Evidence:** runtime confirmed. `TgCommands.cs:1429` equates blank extracted text plus non-text kind with a service event. Probe supplies ordinary uncaptioned photo, poll, video note and all pass. Real uncaptioned voice/animation/stickers/location/contact messages also satisfy the heuristic. `MessageFiles.GetMessageText` is deliberately limited (`MessageFiles.cs:66-78`).

**Expected:** only genuine service message content types. **Actual:** legitimate user content is mixed into service results, in both CLI and MCP. Add positive/negative matrix across service event, text, photo, voice, video note, poll, location, sticker. Root cause is content-text absence used as semantic classification.

### R8 — P2: local-only statistics can claim an exact total

**Evidence:** runtime confirmed. Synthetic local cache [100], [] yields Complete=true/history_exhausted in `ChatHistory.FetchPagesAsync`; `ExportSupport.cs:76-89` does not distinguish local exhaustion from full Telegram exhaustion. `TelegramOperations.cs:64` consequently chooses count_kind=exact. MCP message pagination explicitly marks local exhaustion incomplete (`McpTelegramBackend.cs:78`), so surfaces disagree.

**Expected:** distinguish cache count from account history count. **Actual:** total-looking exact stats with no local-scope field. A probe only proves missing distinction; it does not assert the user's cache is partial. Add partial cache versus server history and empty local cache tests.

### R9 — P2: channel metric aggregates overflow with legitimate totals

**Evidence:** runtime confirmed. `TgCommands.cs:742-745` uses Int32 LINQ Sum for views/forwards/replies/reactions. Two rows with 1.5 billion views each throw OverflowException. Much smaller per-post views cause same issue over many posts. Default max-pages1000 can expose up to100000 posts, so aggregate threshold is realistic for a large channel.

**Expected:** 64-bit totals. **Actual:** JSON metrics export aborts at aggregate construction; JSONL/CSV avoid this particular aggregate function. Add sums above Int32.MaxValue and review numerator int addition at736.

### R10 — P2: `--server` chat search help promises the wrong search scope

**Evidence:** source/contract confirmed. `TgCommands.cs:79` says it also asks servers for public chats; `TelegramOperations.cs:13` switches to SearchChatsOnServer instead of adding public results. This API searches already-known chats on the server, not arbitrary public chats. Expected by documented help: discover unknown public channels; actual: no such search. Missing test/documented contract for local vs server vs public scope. [SearchChatsOnServer](https://core.telegram.org/tdlib/docs/classtd_1_1td__api_1_1search_chats_on_server.html).

### R11 — P2: context bounds can construct invalid requests and conceal incomplete chains

**Evidence:** source confirmed for invalid bounds. `TelegramOperations.cs:84` uses -after without capping, while limit is capped100 at85. `chat context --after100` sends offset-100 although TDLib requires >=-99. Negative before combines with positive after to make limit smaller than -offset; large int additions can overflow before clamp. Context performs one window request, which may be short, and does not refill it. The separately fetched target for reply traversal is not added to messages (`91-100`), so an underfilled window can lack the target even though fetching it succeeded. Reply fetch failures are silently swallowed and 100-depth truncation is not reported.

**Expected:** valid before/after request window and explicit bounded/incomplete reply chain. Missing tests: after100, negatives, int overflow, target missing from window, short window, inaccessible ancestor, cycle. Only the request-construction issue is deterministic without a fake transport; omitted target/short windows need transport simulation. [getChatHistory](https://core.telegram.org/tdlib/docs/classtd_1_1td__api_1_1get_chat_history.html).

## Questions requiring integration or additional focused review

- **Freshness:** CreateReadyAsync/connection readiness is not evidence of a fresh metric counter, chat list, edit or deletion. No explicit counter refresh or snapshot-as-of contract exists in metrics. Assess after checking the actual TDLib caching/update behavior, using a controlled account only if separately authorized. GetChats is documented as informational; do not claim it never loads anything without implementation evidence. [GetChats](https://core.telegram.org/tdlib/docs/classtd_1_1td__api_1_1get_chats.html).
- **Links:** generated group link is `tg://openmessage?user_id=-100...`, while official portable group/channel message links use resolve/privatepost syntax. A valid HTTPS message link may still exist beside it. Treat broken click behavior as a client compatibility hypothesis until tested, not a demonstrated client failure. `link message --plain` always chooses the generated tg link. [Official links](https://core.telegram.org/api/links).
- **Metric semantics:** `has_comments` currently means reply_count>0, which differs from whether commenting is enabled with zero comments. Null InteractionInfo becomes all zeros; no unavailable/not-refreshed marker. `link_domains` misses formatted text-url entities and duplicates repeated domains. Paid reactor aggregation in ExportSchema deserves separate review against the actual full reaction total.
- **Dates:** since/until use DateTimeOffset.Parse without explicit UTC/date-only contract (`TgCommands.cs:892`); date-only until means midnight, not end of day. The same string without zone can shift by machine timezone. Validate ranges before network fetch; distinguish intended semantics from implementation failure.
- **Request lifetime:** whole-chat messages/stats expose requestTimeout but never apply it to history requests. Messages --all does not pass its CancellationToken to ChatHistory; one-page history also awaits without cancellation. Session/timeout owner should integrate this into its broader assessment.
- **Errors:** optional metadata lookups catch all exceptions and return null/empty, so unsupported metadata and transient failure are indistinguishable. History failure in comments after initial thread lookup aborts all output; initial thread failures become inaccessible rows and success. No retry/rate-limit/cancellation classification there.
- **Limits/IDs:** integer TDLib IDs are accepted directly in most CLI reads, while comments intentionally accepts public post ID and shifts it; no explicit conversion validation/overflow check. Zero/negative page budgets and limits are silently normalized to1 rather than rejected. Counts describe fetched messages, attachment type only filters attachment totals, not overall count; document this.
- **Ordering:** Message Get TSV does not sanitize sender_name; context's reply ancestors append after window; comments only sorts each page, not whole thread. These should have explicit output contracts, especially for consumers relying on chronology.

## Simplification opportunities grounded in the findings

1. Centralize page results as items + next cursor + complete + reason + pages, sharing fetchers between CLI and MCP. Keep history cursor semantics distinct from server-provided search/topic cursors. This removes three different short-page EOF implementations and the CLI/MCP disagreement.
2. Replace reflection for TDLib fields present in the pinned package with typed access; migration would become a compile error instead of silent null. Restrict compatibility reflection to genuinely optional fields, with explicit unsupported status.
3. Validate formats, bounds, date ranges and mutually incompatible options before opening Telegram. Use one read-operation cancellation/timeout strategy; avoid exposing ignored options.
4. Separate service-content classification from display text extraction. Separate public post IDs from opaque TDLib message IDs in names/types and schemas.
5. Preserve fetch completeness through metrics/comments/stats; avoid converting unknown or unrefreshed metadata to an unqualified zero. Retain errors for optional enrichment separately from content data.
6. Remove unused `ChatCommands.FetchHistoryAsync` (`TgCommands.cs:1313`) after confirming no generated/dynamic caller; eliminate duplicate date/sender/link serialization helpers only alongside a shared schema contract.

## Test gap summary

Existing tests cover important helper internals, including revised ChatHistory cache pagination, exports, and MCP cursors. They do not exercise the CLI facades with fake TDLib transport. Highest-value added evidence would be short/empty continuation pages, thread roots with newer comments, migration traversal from both ends, cursor/offset plumbing, cap exhaustion, partial local caches, service filtering matrix, and counters above Int32 range. The forum short-page test must be corrected before adding more tests that repeat its assumption. Full live Telegram correctness remains untested by this audit.
