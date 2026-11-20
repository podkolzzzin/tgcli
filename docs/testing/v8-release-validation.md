# tgcli 8 release validation

This record tracks evidence for the candidate and release gates. See the [remediation matrix](v8-remediation-coverage.md) for audit, acceptance and historical-flow coverage, and the [migration guide](../v8-migration.md) for script changes.

## Executed checks

| Scope | Result observed | What ran |
| --- | --- | --- |
| Integrated Linux Debug suite at the CLI/MCP core parity stage | 432 passed, 0 failed, 0 skipped | .NET tests against synthetic native transports, isolated filesystem fixtures and subprocesses. |
| Integrated Linux Debug suite after bot lifetime, media supervision and tracked skills | 437 passed, 1 failed, 0 skipped | `CliHostProcessTests.Global_timeout_bounds_real_lock_wait` expected empty stdout while diagnostics returned a structured report. The recorded failure is an output assertion. The final run needs to verify the held-lock deadline and the intended diagnostic shape. |
| Integrated Python tools on Linux | 24 passed | Installer preservation on HTTP, truncated transfer, checksum and startup failures. Release checks reject mismatched versions, missing RID assets and changed draft files. |
| Linux Debug MCP parity executable | Passed modern and legacy stdio and 10 simulated installer configurations | Temporary app profiles, synthetic clients and missing credentials. The smoke used the updated structured unhealthy diagnostic expectation. |
| Linux media lane native and packaged executables | Passed generated speech in WAV, OGG, MP3 and MP4, language detection, plain output and explicit missing/no-audio errors | Actual embedded FFmpeg and Whisper. The separate native probe exercised Ukrainian speech and SIGINT during inference with cleanup. The packaged build exercised embedded assets with empty PATH. |
| Linux media lane failure injection | Passed SIGKILL during staged batch copy and restart. Passed external supervised descendant termination in a self-contained build. | The interrupted copy preserved previous final files. The supervisor passed exact arguments with empty PATH and terminated an owned pipe-holding descendant while an unrelated process survived. |
| Tracked skills | 31 isolated CLI fixture checks on integrated Debug 8.0.0, plus MCP stdio catalog/preflight | All three tracked skill references match. Independent SOL evaluations used the same synthetic scenario. The v7 reference led to retired flags, schema 5 and integer topic IDs. The v8 reference led to a verified snapshot refresh, scoped checkpoint delta, string IDs, continuation after an empty page and caution after an uncertain mutation. |
| Installed Linux 7.0.1 baseline | Read-only diagnostics observed `ConnectionStateReady` from an authorized profile | Baseline observation from the older installed executable. Controlled first-call freshness, CLI/MCP parity and edit/delete checks require the v8 candidate and a separate fixture. |

The full supported-platform and live checks below remain open at this evidence snapshot. Integrated handler adoption must be backed by named tests, especially native cancellation ownership, channel/media handlers, the unknown participant-count sentinel and structured diagnostics. Diagnostics can publish observed state on an unhealthy exit. Its timeout and lock report require a bounded subprocess test. Further MCP review findings around negotiation metadata, duplicate configuration keys, non-BMP TOML escaping and unreadable sibling Eclipse profiles also await integrated regression evidence.

## Release candidate and publication gates

| Gate | Status at this evidence snapshot | Evidence to record |
| --- | --- | --- |
| Final integrated source | Pending final verification | Source commit, Release test command and counts, Python tests, diff/secret review, and confirmation the original dirty checkout was preserved. Update the held-lock diagnostics result after its regression runs. |
| Linux x64 integrated package | Pending | Candidate SHA-256, byte length, version, exact source and CLI/install/transcription/MCP smoke results in isolated profiles. Recheck the documented skill examples against this binary. |
| Intel macOS x64 hosted package | Pending | Hosted run URL and job result. Verify packaged CLI, verified install, transcription, MCP and runtime platform behavior. |
| Windows x64 hosted package | Pending | Hosted run URL and job result. Verify packaged CLI, verified install and inherited ACL, transcription, MCP and platform process behavior. |
| Draft and downloaded artifacts | Pending | Tag/source match, draft URL or workflow run and SHA-256 for all three binaries. Record that the same uploaded artifacts were downloaded and verified. |
| Public release and actual installation | Pending | Published release URL, verified asset at the intended runtime path, observed `tgcli --version`, and the installed skill update and verification if applied. |
| Live Telegram acceptance | Pending setup and execution on disposable, authorized fixtures | Sanitized result by scenario for CLI and MCP. See the fixture requirements below. |
| Real MCP application acceptance | Pending | Supported client and version, persisted launch values, restart and tool listing. The isolated profiles establish configuration behavior and protocol startup. |

[Package workflow](../../.github/workflows/package.yml) builds and checks the exact x64 artifacts using the [shared build recipe](../../tools/build_release.py). The [release workflow](../../.github/workflows/release.yml) waits for the package matrix, checks the candidate tag and notes, validates uploaded file digests and reads downloaded draft assets back before publication. [Release checks](../../tools/release_checks.py) validate source metadata, asset names and checksums. The [installer](../../tools/install.py) verifies an asset before replacing the executable. This release path uses GitHub and its checksums as the distribution trust boundary. The x64 matrix does not establish ARM runtime coverage.

A Linux x64 comparison on the pre-integration package measured about 388 MB uncompressed and 231 MB compressed. The uncompressed and compressed cold `--version` launches took approximately 0.11 and 1.47 seconds respectively, with peak RSS about 53 and 460 MiB. The comparison used the same source, runtime and assets, and did not open Telegram. The build recipe uses one uncompressed bundle. Final artifact measurements belong in the candidate row above.

On Linux, the external transcription supervisor can clean descendants within its owned process group. External transcribers on Windows and macOS must run synchronously and wait for their children. A launcher that exits while a descendant still holds a pipe can leave cleanup unverified there. Explicitly detached Linux descendants also fall outside the supervised group.

## Live Telegram acceptance record

The harness preparation used isolated synthetic storage and performed no Telegram mutations. A private run needs an authorized disposable fixture and a second controlled client. Keep this record to scenario labels, pass/fail and technical counts. Keep credentials and account, chat and message details in the private fixture environment.

| Scenario | Required observation | Status at this evidence snapshot |
| --- | --- | --- |
| A01, first request after a new marker | Start with pre-marker cache states and use the first normal v8 CLI and MCP reads. Observe the marker in the requested scope or a classified failure. Repeat cold, warm and stale. | Pending |
| A03, network interruption and recovery | Observe bounded failure or explicit partial error during loss. An explicit cache request stays cache-only. A later online request succeeds after reconnection. | Pending |
| A06–A09, scope, counters, edits and deletes | A migrated or linked fixture keeps per-source identity and scope. Observe known counters, then apply a controlled edit/delete and verify repeated same-scope snapshots and local preservation. | Pending |
| A11–A13, contention, revocation and bots | Verify bounded real contention and terminal auth. On an authorized disposable bot, verify current prompts and receipts and capture only statuses. | Pending |
| A17, file references and citation | Observe current attachment selection and verified download after controlled reference turnover. Check links on the relevant client. | Pending |

TDLib's known state can lag a server change. `tdlib_update_state` provenance and classified incompleteness matter when a marker or scope cannot be established. Tombstones describe inferred absence within a verified accessible scope.
