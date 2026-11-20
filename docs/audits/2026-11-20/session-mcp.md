# Session, connection, secrets and MCP audit (2026-11-20)

Read-only source audit of `/home/andrii/RiderProjects/tgcli`. No repository edits, account access, live TDLib client, real session data, installed MCP client configuration, or installer registration was performed. Temporary probes under `/tmp/tgcli-session-probes` compiled directly linked current source and used only fabricated session directories. Root runs the full suite. Installer/discovery subreport: [installers](installers.md).

## Coverage and existing protections

Read TelegramSession.cs, TelegramConnection.cs, SessionSecret.cs, McpSessionGate.cs, McpTelegramBackend.cs, McpTools.cs, McpSchemas.cs, McpMessagePage.cs, McpServerHost.cs, McpCompatibilityTransport.cs; reviewed relevant callers in TgCommands.cs, TelegramOperations.cs, MessageLinks.cs and ExportSchema.cs. Reviewed all TelegramSessionTests, TelegramConnectionTests, McpSessionGateTests, McpToolsTests, McpProtocolTests and the fake MCP test server. Installer files/tests delegated separately.

Preserve the user's dirty 7.0.1 work: authorization restored from disk is separated from actual connection/update readiness; online CLI starts wait for connection; MCP rechecks readiness before every online operation; explicit local mode bypasses readiness; connection state is reported in diagnostics. TelegramConnection's lock/TCS design handles reconnects and readiness updates arriving before wait. Five tests cover initial sync, ready-before-wait, reconnect, timeout and cancellation. No source regression found in this new class.

MCP has useful existing safeguards: lazy shared session, serialized native use, queue-inclusive request budget, canceled native work retaining the gate, retry after failed initialization, stdout reserved for JSON-RPC, no SDK payload logging, typed argument bounds, decimal-string 64-bit IDs, cursor filter binding and progress checks, full duplicated successful result size cap, explicit output schemas, operation read/write annotations, and CLI-independent structured responses. These should remain intact while fixing gaps.

## Confirmed findings

### S1 — Session secret import/export does not hold the database lock across the operation (high: possible active-database replacement)

Refs: SessionSecret.cs:102, 125, 127-135, 217-225; SessionSecret.cs:302-307; TelegramSession.cs:323-339.

Trigger: secret import or export probes an idle session; another tgcli process acquires `tdlib.lock` after the probe returns and before the files are read/replaced. Import `--force` then recursively deletes the session directory, including the lock pathname; on Unix the other process still holds the unlinked inode while a third process can create/lock a new inode at the same pathname. Export can instead read a changing authorization binlog.

Expected: secret operations reserve the same exclusive lock for the whole snapshot/restore, with no deletion of the coordination inode while owned. Actual: `EnsureSessionUnlocked` calls `SessionLock.Inspect`, whose `using` stream is already disposed before returning; force removes the entire directory. Source-confirmed deterministic interleaving; not executed against any live database. This is distinct from safe `session unlock --stale-only`, which holds its lock while deleting metadata.

Fix plan: extract a reusable lock lease shared by session and secret operations; acquire it for all read/replace work; stage imported files without deleting the lock inode; verify with multi-process temporary-directory race tests and failure injection.

### S2 — Lock acquisition failures can leak the acquired exclusive file handle (medium)

Refs: TelegramSession.cs:330-342 and 42 (AcquireLockAsync happens before CreateAsync's cleanup try).

Trigger: fabricate a session whose `tdlib.lock.owner` is a directory, then call AcquireLockAsync. FileStream creation and lock succeed; writing metadata fails. Expected: release lock, surface the metadata/filesystem error. Actual: UnauthorizedAccessException escapes with the FileStream still undisposed; subsequent exclusive open fails. IOException failures later in acquisition similarly retry against the handle they leaked and can misreport contention. A long-lived MCP process may retain this until GC/process exit, so retry is unreliable.

Executed temp-only probe output:
```
acquire_failure=UnauthorizedAccessException
lock_released=False
```
Harness: `/tmp/tgcli-session-probes/probe.csproj`, `/tmp/tgcli-session-probes/Program.cs`; command `dotnet run --project /tmp/tgcli-session-probes/probe.csproj --nologo`.

Fix plan: dispose any opened stream on every unsuccessful acquisition, and isolate lock contention errors from metadata write errors. Add a same-process temporary filesystem test proving immediate reacquisition after injected owner-write failure.

### S3 — Force import validates the archive wrapper but not config before deleting the old session (medium)

Refs: SessionSecret.cs:119-135, 171-197; TelegramSession.cs:528-544.

Trigger: manifest-valid ZIP containing the three allowed files, but `config.json` is not JSON (or does not contain usable credentials); import with `--force` over a previous session. Expected: malformed config rejected before previous session is removed. Actual: archive wrapper passes validation, old directory is deleted, arbitrary config/binlog bytes are written, and import returns success; next session creation fails parsing config. Binary TDLib authorization validation may require TDLib, but the config syntax/fields are already knowable without native access.

Executed temp-only probe: valid manifest plus `config.json = definitely not JSON`, dummy binlog, and a preexisting sentinel. Output:
```
invalid_import_accepted=True; original_preserved=False
```
Same probe harness as S2. No actual auth data used. The destructive nature of explicit `--force` is intended; accepting a known-invalid configuration as a successful replacement is the defect. This also motivates staging/atomic promotion so write failures do not discard the last valid session.

Fix plan: validate parseable and usable config before touching target; stage complete files privately, then replace under the S1 lock lease with rollback on failure. Test malformed/missing-credential config and failures between staged writes/promotion. Avoid claiming arbitrary binlog contents can be validated without TDLib.

### S4 — Interactive login spins forever when stdin reaches EOF (medium)

Refs: TelegramSession.cs:415-425; login prompts at 105, 121, 126, 130, 266.

Trigger: login needs a nonempty phone/code/email but stdin is closed or a pipe ends. Expected: explicit input-ended/canceled error and session cleanup. Actual: `ReadLine() ?? string.Empty` treats EOF as an empty attempt; while(true) immediately repeats and floods prompts indefinitely. `PromptSecret` also assumes a console (`Console.ReadKey`) and has no piped-input strategy, but that is a separate compatibility limitation.

Executed reflection probe supplied 10 null EOF reads and threw on read 11 solely to bound the test. Output `prompt_eof_reads=11; prompts=12` (the split count includes its leading empty element). No TDLib/login invoked.

Fix plan: distinguish null from blank input and throw a clear cancellation/input exception; preserve retry for deliberate blank lines. Test finite blank input followed by valid input and EOF.

### M1 — Malformed initialize request prematurely commits legacy transport mode (low/medium protocol correctness)

Refs: McpCompatibilityTransport.cs:31-34 versus validation at 46-59.

Trigger: fresh fake MCP server receives `initialize` with `{}` (missing mandatory fields), then `tools/list` without modern metadata. Expected: failed initialize leaves protocol unnegotiated; second request should still require a valid initialize or modern metadata. Actual: malformed initialize returns `-32603`, yet subsequent metadata-free `tools/list` succeeds because `_mode = 2` was set before SDK validation. A modern session can likewise receive a successful legacy initialize response while `_mode` remains modern, then reject metadata-free legacy calls. Protocol invariants become inconsistent; not a Telegram authorization bypass.

Reverified against the freshly built Debug stdio server with fake backend only; retained [script](evidence/protocol-probe.py) and [output](evidence/protocol-probe.txt): `TgCli.Tests/McpTestServer/bin/Debug/net10.0/TgCli.McpTestServer.dll`.
```
malformed_initialize:
1 initialize {} -> error -32603
2 tools/list {} -> result keys ['tools']
3 tools/list with modern metadata -> modern result
switch_after_modern:
1 tools/list with modern metadata -> modern result
2 valid legacy initialize -> successful initialize response
3 tools/list without metadata -> -32602
```
No stderr or native/account calls. Existing tests cover invalid first metadata/version but not invalid initialize or switching handshake mode.

Fix plan: commit negotiation mode only once initialization succeeds, or reject unsupported mixed negotiation explicitly. Add fake-server tests for malformed initialize, retry after failed initialize, and attempted modern/legacy mode switching.

## Source-confirmed gaps / hypotheses requiring targeted validation

- Local read policy is not propagated through enrichment. `McpTelegramBackend.cs:22` skips readiness for local messages, then `:161-162` always asks ExportSchema for links; `ExportSchema.cs:21-23,178-187` resolves replies with GetMessageAsync and MessageLinks.cs:43 requests links. Only the history request itself uses `onlyLocal`. Uncached reply/link enrichment may make nominally local operation need network or time out. TDLib package docs explicitly describe GetChatHistory(only_local=true) as offline and provide a separate GetMessageLocally API. Confirm exact network behavior with a fake TDLib facade or controlled offline integration; do not claim this was live-reproduced. Same enrichment issue affects CLI rich messages. Local stats also fetches participant full info in shared code (root agent coordinating this).
- Cancellation does not reach session initialization/lock wait (`CreateAsync`, `CreateReadyAsync`, InitializeAsync, EnsureReadyAsync lack token). EnsureReady's 20-second deadline does not bound `Client.GetAuthorizationStateAsync` itself; InitializeAsync has no deadline. MCP safely times out the caller while native work holds the gate, but an indefinitely stuck create can strand all subsequent tool calls. CLI commands can wait through lock/startup despite cancellation. Existing gate tests prove serialization with fake resources, not actual native initialization/shutdown. Preserve the rule that native use must finish before releasing lock.
- Reused MCP sessions recheck connection but never revalidate authorization or replace a session that has become terminal/closed after successful initial creation (McpSessionGate.cs:33; McpTelegramBackend.cs:21-23). Connection reconnection is supported; auth revocation/terminal TDLib recovery needs a defined behavior and tests. Do not report permanent failure as reproduced.
- Authorization updates/TCS fields in TelegramSession are not protected by the synchronization used by TelegramConnection. GetAuthorizationState's response is written into `_authorizationState` after await while UpdateReceived also writes it (`222-250`, `399-402`). An older response could overwrite a newer update; requires controlled response/update ordering tests before asserting a missed-wakeup bug.
- Successful result cap is enforced, but error text returned by McpTools.cs:44-47 is uncapped. An extremely long unknown argument name is echoed by validation (`:124`), allowing a large error result beyond 512 KiB. Static path identified; not separately executed. Consider bounded error formatting with a test if “all responses capped” is intended.
- Entire secret payload, each decompressed entry, and duplicate-path dictionary are read before allowed-entry checks (`SessionSecret.cs:153-197,242-247`). No payload/decompressed size/count cap. Malformed ZIP/duplicate normalized names surface generic errors. Treat as local input robustness work, not an external-account security incident.
- Connection readiness is checked at operation entry, so a mid-operation disconnect is not covered by the new readiness class alone. This is expected TDLib behavior; offline/error behavior must be tested at operation boundaries, not “fixed” by releasing session locks early.

## CLI/MCP parity map and intentional differences

| Area | Shared behavior | Differences/gaps |
|---|---|---|
| Session ownership | Same TelegramSession exclusive disk lock | CLI creates/disposes each invocation; MCP lazily retains one session and serializes calls |
| Readiness | New connection tracking used by both | CLI once at creation; MCP before each online operation; local only history/stats |
| Cancellation | Shared lower-level native APIs | MCP queue-inclusive 1-300s outer request budget and retention gate; CLI coverage depends on command and is uneven |
| Chat list/search/resolve | TelegramOperations and Output chat rows | MCP bounded envelopes and string IDs; CLI formats and limits differ intentionally |
| Messages/search | Same TDLib operations and FilterMessages | MCP paged opaque cursor; CLI raw offsets/all-pages; MCP always rich schema/enrichment |
| Context/stats | TelegramOperations | Context CLI passes CancellationToken.None; MCP caller token provided; stats MCP retains native operation on timeout |
| Links/download | Shared MessageLinks and download helpers | MCP only explicit absolute output, overwrites declared; CLI supports more selectors/relative output |
| Diagnostics | Both expose observed readiness/state | CLI adds TDLib/database/latency/account fields; MCP reduced flat identity schema; both require ready session first, so not a structured offline diagnostic |
| Auth and account actions | CLI only | MCP intentionally omits login, bot writes/tokens, secret import/export, bulk export/download and transcription |

## Test coverage assessment and staged plan

Session tests cover only three code-delivery descriptions; ExportTests does cover SessionSecret allowed files/import overwrite refusal and stale/active lock metadata. There are no tests for real lock failure cleanup, force-import crash/race safety, EOF prompts, config precedence, auth state transitions, terminal-session recovery or native shutdown bounds. Connection tests are good targeted unit coverage for the new 7.0.1 synchronization. Gate tests prove single creation/serialization, timeout retaining native gate, retry initialization, tool failures retaining session, and cooperative shutdown, but not drain-timeout cleanup or delayed create/terminal sessions. Tools tests cover bounds/64-bit IDs/cursors/conversion/cap/schemas/pagination helpers. Protocol tests use fake Telegram data: they prove framing/metadata/cancellation/server usability, not real output-schema validity, Telegram permission errors or offline behavior.

Proposed order, no implementation undertaken:
1. Keep dirty 7.0.1 fixes and establish baseline with root's full tests.
2. Add temporary-only failure/race tests and fix the shared lock lease + secret import transaction (S1-S3 together at the storage lifecycle boundary).
3. Fix finite EOF handling and define cancellation/deadline propagation for startup without detaching native work.
4. Add an injectable TDLib operation seam sufficiently small to test local enrichment, mid-operation disconnect, auth loss and slow native calls. Share typed result/operation policies between CLI and MCP; avoid a broad command rewrite.
5. Add invalid-initialize mode tests and correct compatibility transport negotiation state.
6. Installer fixes and profile serialization tests from the attached subreport.
7. End-to-end validation separately with explicitly authorized disposable sessions and actual clients/OSs; fake server tests must remain categorized as protocol coverage.

Simplification opportunities: unify SessionConfig and SessionSecret's duplicated default-path logic; factor one lock lease (currently session acquisition, inspect, repair and secrets each reason about it separately); centralize deadline/cancellation policy instead of repeated nested timeouts; model CLI/MCP result conversion on explicit ID field types rather than a generic suffix heuristic; normalize effective cursor filters rather than raw caller JSON if equivalent omitted/default values should interoperate. These are secondary to confirmed defects and should be narrowly scoped.
