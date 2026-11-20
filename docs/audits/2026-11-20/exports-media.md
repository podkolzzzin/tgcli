# Export, attachments, downloads, media, and bot audit

Scope: `/home/andrii/RiderProjects/tgcli`, read-only source audit of working tree (including existing dirty edits). No native Telegram client was instantiated, no Telegram calls were made, no media was downloaded or transcribed, and no repository file was edited by this audit. Temporary offline C# harness: `/tmp/tgcli-exports-media-probe/Probe.csproj`; captured evidence: `/tmp/tgcli-exports-media-probe/output.txt`. Run again with `dotnet run --project /tmp/tgcli-exports-media-probe/Probe.csproj --verbosity quiet`. It references the existing Debug DLLs, uses `TgCli.Tests` friend assembly identity, and never constructs a native client. Root owns full-suite validation.

Evidence labels: **runtime helper** means actual compiled repository helper invoked with synthetic input. **source replay** means literal control flow from a command replayed with synthetic data; this is not a full command integration test. **source confirmed** means the ordering/condition is explicit, without operating a live account. **hypothesis** means external protocol/environment determines occurrence.

## Coverage

| Surface | Read paths | Offline evidence / existing tests | Remaining boundary |
|---|---|---|---|
| History export, pagination, migration | `ExportSupport.cs`, `TgCommands.cs:1147-1263` | Runtime helper local completeness, cancellation, manifest guard; existing `ChatHistoryTests`, manifest tests | Native history semantics and real migration are untested here; reads audit owns migration-property defect |
| Resume / incremental / projection | `TgCommands.cs:1176-1263,1444-1455`, `Output.cs:126-192` | Source replay of edit/delete/repeated-delete/invalid token; runtime projection helper | No existing command-level resume/incremental tests; no native command run |
| Export schema and attachment metadata | `ExportSchema.cs`, `MessageFiles.cs` | Runtime primary-vs-export metadata; schema/tombstone/metric tests inspected | Sender, reply preview, forwarding and public-link lookups require fake client seam or live read |
| Single download and ID recovery | `TgCommands.cs:99-122,232-405`, `AttachmentIndex.cs` | Existing destination and index round-trip tests; source trace of cached thumbnail recovery | No fake download/client tests; no actual downloads |
| Batch download | `BatchDownloads.cs`, `TgCommands.cs:133-229` | Runtime duplicate alias + same output destination; parser/parallel-limit/path tests inspected | Scheduling failure, cancellation and partial-copy behavior have no tests |
| Local / message / export transcription | `MediaTranscription.cs`, `MediaDecoder.cs`, `WhisperMediaTranscriber.cs`, `EmbeddedTranscriptionAssets.cs`, `TranscriptionCommands.cs`; command call sites | Existing media-kind, validation, decode cleanup/cancel, asset hash tests inspected | Actual model accuracy/performance and message download path not exercised; root owns existing E2E |
| Bot create/remove/token/write/list | `BotCommands.cs` | Source ordering review; actual token-extraction helper with synthetic string | No bot tests found. No bot sends, callbacks, token operations or account mutations performed |

## Prioritized findings

### E1 — Local-only history can issue false deletion tombstones (P1)

**Runtime helper + source confirmed.** `ChatHistory.FetchPagesAsync(... all:true, local:true ...)` with an empty local page returns `Complete=true`, `TerminationReason=history_exhausted`. Local exhaustion does not establish that the server lacks a message. Export deletion detection checks `incremental && history.Complete` and absence of filters, but does not require a server-complete scan.

Reproduction: existing cache contains `(chat=1,message=2)`; local database has no messages; run the reconciliation branch corresponding to `--incremental --local`. Expected: preserve old record without a deletion assertion or mark local completeness separately. Actual: the key is absent from fetchedKeys and gets `is_deleted=true`/`missing_from_incremental_refresh`. This is deterministic given an empty local cache, with no need to assume a Telegram bug.

Root: `ExportSupport.cs:76-89,145-149` only makes local completeness special when history did not exhaust, while `TgCommands.cs:1231-1233` trusts one global Complete flag. Runtime output: `empty-local-history complete=True; termination=history_exhausted`.

Plan: distinguish reachability/local exhaustion from authoritative server deletion evidence; forbid tombstone generation for local or otherwise incomplete scope. Add a fake-history test with nonempty prior cache + empty local page.

### E2 — Repeated incremental refresh is not idempotent; manifest counts become false (P1/P2)

**Source replay, exact lines.** Cache reading adds every row's key to one HashSet, including tombstones (`TgCommands.cs:1218-1226`). Every later absent key becomes a new tombstone (`1231-1244`); copied old rows remain (`1237-1239`). Counts use unique keys instead of physical rows (`1250-1253`).

Reproduction: start with one live row; complete refresh finds none; perform another identical refresh. Expected: second refresh adds no redundant deletion and manifest accurately describes its file. Actual replay: first refresh = 2 rows and reported 2; second = **3 actual rows, reported 2**, one additional tombstone each run. A reappearing key is also removed by `history.Messages.RemoveAll` (`1230`), so a prior tombstone can never be superseded by the reappearing message.

Plan: define a keyed latest-state reducer for cache rows, compare prior live state with fetched state, emit only transitions, and explicitly distinguish physical event rows, live messages, and newly written rows in manifest. Add repeat-run idempotence, delete/reappear, duplicated historical events, and count tests.

Related semantics gap: an edited message with same ID is always removed before export; source replay retains `text=old` when fetched text is `edited`. README describes deduplication plus tombstones rather than explicitly promising edit upserts, so treat this as a documented behavior/requirements decision, not the strongest standalone contract violation. A useful “incremental refresh” should either upsert edits/metrics or explicitly describe immutable append semantics.

### E3 — Bot write sends to ordinary users before verifying they are bots (P1)

**Source confirmed; no live reproduction.** `Write` first resolves only private chat type (`BotCommands.cs:155-156,191-199`), then sends the requested text (`159-162`) and/or invokes a callback (`165-170`). `ResolveBotUserAsync` checks `UserTypeBot` only while constructing output (`176,202-211`).

Synthetic condition for a future fake transport: SearchPublicChat returns a private human user, GetUser returns UserTypeRegular, text is nonempty. Expected: bot validation fails with zero SendMessage calls. Actual control flow: SendMessage is invoked, then command throws “is not a bot.” This is a concrete unwanted external side effect on an invalid command.

Plan: resolve and validate bot identity once before any send/callback, reuse result for output. Add call-order and zero-mutation-on-validation-failure tests.

### E4 — Invalid bot output format is rejected only after remote mutation (P1/P2)

**Source confirmed.** `Create`, `Remove`, `Token(revoke:true)`, and `Write` all call `PrintObject` at the end. Only `PrintObject` validates format (`BotCommands.cs:476-492`). For example Create sends `/newbot`, name and username at `23-28`; `--format invalid` fails later at `41/492`.

Expected: format validation before remote operations. Actual: bot creation/deletion/token revocation/message send may complete and the CLI then reports an argument error, inviting unsafe retries. `Remove` correctly checks its explicit `confirm` guard before opening the session (`53-56`), showing the appropriate pattern already exists.

Plan: central pure format parser and complete option validation before constructing the session; assert invalid format produces zero calls for each mutating method.

### E5 — Structured exports lose available media metadata and mislabel thumbnails (P2)

**Runtime helper.** Create a MessageVideo with filename `lecture.mp4`, MIME `video/mp4`, 1920×1080, duration 42, file ID 10, and thumbnail ID 11. Primary helper returns all metadata correctly. `GetAllFiles` (the actual schema path) returns ID 10 with filename/MIME/width/height/duration all null; ID 11 has `kind=video`, also loses thumbnail dimensions. `ExportSchema.BuildMessageAsync` serializes this lossy collection (`ExportSchema.cs:27-32`).

Root: reflection reaches a `TdApi.File`, then attempts media metadata properties on that File instead of the parent Video/Thumbnail (`MessageFiles.cs:150-166`). Kind inference tests parent “video” before “thumbnail” (`215-255`). Existing duration test verifies `GetPrimaryAttachment` only (`TgCli.Tests/ExportTests.cs:122-143`), missing the actual export route.

Plan: use typed metadata extraction with explicit parent context, merge primary metadata by file ID, and mark thumbnail role before parent media kind. Test schema rows from real-shaped voice/audio/video/photo messages, not manually constructed MessageFile records.

### E6 — Resume cache identity and token validation are incomplete (P2)

**Runtime helper + source replay.**

* Projection helper permits `--fields text`, retaining only schema fields and text (`Output.cs:184-192`). Cache ingestion later reads missing IDs with `Value<long>` yielding `(0,0)` (`TgCommands.cs:1220-1222`). Runtime evidence produced `{"schema":"tgcli.message","schema_version":"5.0","text":"hi"}` and subsequent key `0:0`. Expected: resume-capable exports preserve identity, or resume explicitly rejects incompatible rows. Actual: fetched real messages are not deduplicated; incremental mode can append a tombstone for `0:0`.
* Resume parser splits a token but validates only the message-ID suffix (`TgCommands.cs:1179-1189`). Replay accepts `wrong-chat:900`; messages with IDs ≤900 are filtered for **every source chat**, without checking token chat ID. Expected: validate complete token and scope cursor to its chat/migration segment. Actual: malformed/wrong-chat tokens are accepted and can silently exclude records from unrelated ID spaces.
* Cache JSON validation only checks syntax (`1444-1455`), not schema, identities, source scope, or active/tombstone state. Using a different chat's output file can mark unrelated cached messages as deleted.

Plan: a versioned checkpoint with source identity and per-chat cursor, strict cache schema/ID validation, and mandatory key fields for appendable exports. Tests: projection without IDs, wrong chat, malformed prefix, migrated chats with overlapping/nonmonotonic IDs, empty/missing file, mismatched schema.

### E7 — Cancellation during a history request is ignored, and can return success (P2)

**Runtime helper.** Inject an incomplete Task as `fetchPage`, start FetchPagesAsync with a token, then cancel. After 100 ms task is still incomplete. Complete injected request with an empty page: FetchPagesAsync returns successfully with `Complete=true` despite cancellation.

Root: cancellation checked before request, but `await fetchPage(...)` is not cancellable and no token check follows it (`ExportSupport.cs:57-89`). `isChannelChat` is also awaited without a cancellable wrapper (`50`). JSON export rendering has no per-item cancellation check (`Output.cs:139-149`); token is used only in the optional transcription path. Batch passes cancellation only to the parallel scheduler and discards each worker token (`TgCommands.cs:165-187`); individual resolution/download awaits cannot observe it.

Plan: propagate tokens to request wrappers, define bounded native-request waits, and check cancellation before output commit. Because a cancelled wait may leave native work active, integrate with the repository's session/gate lifetime policy rather than just adding WaitAsync indiscriminately. Fake pending-request and cancellation-after-final-page tests; JSON rendering cancellation test; batch partial-result policy test.

### E8 — Batch duplicate aliases can race to the same destination (P2)

**Runtime helper.** Two input rows `(chat=1,message=2,type=video)` and `(chat=1,message=2,type=file)` are accepted (2 requests). Both select the same primary video and both resolve to the identical output name. Same issue for accepted aliases such as `voice`/`voice-note`, and null/`file`.

Root: duplicate identity uses raw lowercased type text (`BatchDownloads.cs:71-78`), while kind parsing has aliases and generic kinds; output path ignores type/index (`93-102`). Parallel workers overwrite that path (`TgCommands.cs:194-195`). Expected: deterministic dedup/rejection before starting or disjoint destinations. Actual: redundant native work and simultaneous writes, with timing-dependent failed row or competing overwrite.

Plan: canonicalize attachment selection and detect destination collisions; simplest semantics for current one-primary-file-per-message interface is one request per message. Test alias/null/generic collisions and input result identity.

### E9 — Indexed numeric IDs can resolve to a different file when remote ID is absent (P2)

**Source confirmed, conditional input.** Message inspection indexes *all* nested files (`ExportSchema.cs:35`, `MessageCommands.Get` at `TgCommands.cs:507`). Index may contain thumbnail ID 11 with no remote ID. Numeric download for ID 11 loads cached message, calls GetPrimaryAttachment and returns primary video ID 10 without matching requested ID (`TgCommands.cs:264-268`). Expected: exact requested file or clear unavailability error. Actual: a different payload is selected. The same branch can return an edited replacement attachment under an old file ID.

Plan: cached lookup must match requested FileId within GetAllFiles, using recorded role/type; refresh stale remote references with cached chat/message identity; test thumbnail and replaced attachment. Do not blindly trust a cache entry if remote resolution fails.

### E10 — `--expect-since` passes when export has no dated messages (P2)

**Runtime helper.** `ChatHistory.ValidateManifest(BuildManifest(emptyHistory), 2020-01-01, 0, true)` succeeds. Expected: a requested earliest-boundary integrity check fails when no earliest boundary is known. Root: condition at `ExportSupport.cs:344` only adds failure when first_date parses and is too recent; null/unparseable dates bypass it. This is especially misleading together with local empty history marked complete.

Plan: fail an explicit temporal assertion when its measured value is unknown; test empty/no-date history and exact boundary inclusivity.

### E11 — Bot token parser truncates a trailing `-` (P2)

**Runtime helper.** Invoke existing private ExtractToken on synthetic `12345:abcdefghijklmnopqrstu-`; actual returns `12345:abcdefghijklmnopqrstu`, dropping final `-`. Regex includes `-` in the token alphabet but ends with `\b`, which requires a word boundary and backtracks over trailing hyphens (`BotCommands.cs:506-509`). No real token was accessed. Whether a live BotFather token ends with `-` is not needed to demonstrate parser inconsistency, but live incidence was not measured.

Plan: use explicit token-character lookarounds/end delimiters; tests for every accepted trailing alphabet character and surrounding punctuation.

## Interruption, retry, and partial-file review

* Positive: file exports stage content and rename into place (`TgCommands.cs:1234-1248`), so failure before rename preserves an existing destination. Embedded transcription asset extraction uses unique temp file, checksum verification, rename and finally cleanup (`EmbeddedTranscriptionAssets.cs:41-58`). Media decoder cleanup occurs on failure (`MediaDecoder.cs:35-40`); MediaProcess concurrently drains output/error and kills process tree on cancellation (`MediaTranscription.cs:116-139`), with tests for decoder cancellation and cleanup.
* Source-confirmed limitation: export and manifest are two separate commits (`TgCommands.cs:1248,1262-1263`). Interruption or manifest write failure between them leaves new export + old/missing manifest. README's “fresh writes and manifest updates are atomic” applies individually, not a transactional pair. Fixed `<output>.tmp` survives errors, holds a potentially sensitive partial export, and offers no incremental checkpoint/recovery; next invocation truncates it. Cross-session writers targeting the same output have no output-level lock; this is a concurrency risk, not observed live corruption.
* Source-confirmed limitation: single and batch downloads use direct overwriting File.Copy (`TgCommands.cs:119,195`) with no destination staging. IO failure/process termination during copy can destroy an existing destination or leave a partial final file. Neither command checks IsDownloadingCompleted in final validation (`112,189`), unlike transcription download (`MediaTranscription.cs:85`). The incomplete-native-result scenario is a mockable risk, not observed against Telegram.
* Source-confirmed limitation: batch emits results only after every worker finishes (`TgCommands.cs:216`). Cancellation/failure of the parallel loop can leave already copied files with no output records. A resumable or streaming result contract is absent.
* History retries every non-cancellation Exception twice with 250/500 ms delay (`ExportSupport.cs:67-71`), including permanent/validation failures; final exception discards fetched in-memory progress. No shared error classification/FLOOD_WAIT handling in this scope. Download refresh catches every TdException once and refreshes message (`TgCommands.cs:296-302`), not just stale file references.
* Source-confirmed limitation: Output buffers all export objects/rows before writing (`Output.cs:139-153,158-174`); fetching already buffers all messages. Large transcribed exports have no durable progress until all enrichment/transcription finishes. This is a scalability/recovery design gap, not a measured OOM.
* Hypothesis requiring fake protocol fixture/live read approval: BotFather callbacks may edit a message in place. WaitForBotReply only considers IDs strictly greater than the previous message (`BotCommands.cs:237-240`), so an edited prompt can time out. Write sends and immediately looks up latest keyboard (`159-169`) without awaiting the resulting UI, so delayed replies may select an old keyboard. SendMessage temporary IDs and multi-message BotFather responses also need explicit state-machine fixtures. No live behavior claim is made.
* Hypothesis requiring filesystem/concurrency fixture: AttachmentIndex is append-only, linearly scanned, and opened per rendered message (`AttachmentIndex.cs:16-31,46-68`). Advisory-write failure aborts export; repeated reads append duplicate metadata indefinitely. Session locking may serialize normal callers, so do not claim an exposed concurrent writer defect without a call graph that bypasses it.
* External transcription command treats the entire suffix after its first space as one argument (`MediaTranscription.cs:99-103`). README documents an executable, so this is an interface simplification opportunity rather than a firm documented multi-argument bug. Prefer explicit executable+argument-list contract and never introduce implicit shell execution.

## Test gaps and focused plan

Existing tests have good pure-helper coverage for manifest presence, schema/tombstone fields, media support, basic parser validation, destination format, asset hash integrity, decoder cleanup, and missing-file/pre-cancel paths. They do not exercise export persistence/reconciliation or BotCommands remote-call ordering. The metadata tests exercise the richer primary helper instead of the actual GetAllFiles schema path. The disabled/nonprimary test invokes transcription=null, so it does not independently establish that an enabled transcriber ignores unsupported thumbnail input.

Recommended order:

1. Extract pure command-option validation and verify bot identity before mutation. Add fake transport call-recording tests for E3/E4; no live bot operations needed.
2. Extract export cache/reconcile/write service independent of Telegram. Define server completeness, active state/tombstone reducer, strict identity schema, and per-chat checkpoints; then cover E1/E2/E6/E10 with synthetic snapshots. Decide edit-upsert semantics explicitly.
3. Define export+manifest commit/recovery protocol and use unique staging files with cleanup. Fault-injection tests before/after data rename and before manifest rename; interrupted refresh must leave detectable/recoverable state.
4. Replace recursive metadata guessing for supported media with typed file descriptors; preserve role, parent dimensions/duration/MIME/name and exact file identity. Cover E5/E9 against message-shaped fixtures.
5. Share a cancellable, validated download service across single/batch/transcription. Require complete source files, stage copies, canonicalize batch identity/collisions, and emit/recover completed-row outcomes. Add cancellation and IO-failure injection; coordinate native outstanding-work lifetime with session gate.
6. Add bounded enrichment/streaming and cache sender/chat metadata to avoid repeated lookups; keep one serialization/projection pipeline. Profile before changing pagination or model behavior.
7. Move BotFather flows behind a small conversation state machine with explicit prompt/edited-message transitions and pending-send identity handling. Test scripted replies, reject ambiguous selections, and fix token parser using pure tests. Live checks should be separately authorized and restricted to the specific requested operation.

No implementation has been performed. Source defects and runtime helper observations above are sufficient to draft a focused implementation plan; external-protocol hypotheses should remain labeled until reproduced.
