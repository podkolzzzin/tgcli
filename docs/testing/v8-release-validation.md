# tgcli 8 release validation

This record tracks the source, candidate, and release gates. See the [remediation matrix](v8-remediation-coverage.md) for named audit and historical-flow checks and the [migration guide](../v8-migration.md) for script changes.

## Integrated source and local execution

| Scope | Observed result |
| --- | --- |
| Integrated Linux Debug .NET suite at `caea7e3` | 461 passed, 0 failed, 0 skipped. An intermediate 437/438 run exposed a held-lock test that expected empty stdout after diagnostics began returning a structured report. The passing integrated suite separately covers the ordinary lock timeout and the diagnostic timeout with structured lock evidence. Its synthetic native tests also cover cancellation during enrichment, transitional authorization, exact topic identity and the unknown participant-count sentinel. |
| Independent Linux review of `caea7e3` | 159 focused cases passed across CLI wire, synthetic native handlers and MCP. Raw stdio remained usable after malformed metadata, duplicate keys and malformed Unicode. Independent TOML parsing verified exact supplementary Unicode paths, and inaccessible Eclipse traversal preserved a readable sibling. |
| Integrated Linux Debug .NET suite at `5f5514a` | 466 passed, 0 failed, 0 skipped after Linux native MCP registration supervision. The installer change also passed 169 focused lane tests, 49 independent focused tests, modern and legacy MCP on a self-contained package with 10 fixture configurations, and 12 packaged deadline stress trials with zero late writes. |
| Integrated Python tools on Linux | 24 passed. The installer preserved the previous executable through rejected candidates. Release checks covered mismatched source version and notes, missing RIDs and changed draft files. |
| Skills | All three tracked references match. Independent SOL evaluations with the same synthetic scenario found that the v7 reference led to retired flags, schema 5 and integer topic IDs. The v8 reference led to a verified refresh, scoped checkpoint delta, string IDs, continuation after an empty page and caution after an uncertain mutation. The packaged CLI exercised 31 scenarios in empty synthetic profiles and the MCP probe verified its catalog and validation. |
| Linux media fault injection | Actual SIGKILL during a staged batch copy preserved old final files and a restart reused the verified complete file. SIGINT during embedded inference returned 130 with temporary WAV cleanup. The Linux packaged external transcriber terminated a pipe-holding child after its launcher exited, preserving an unrelated process. |
| Installed Linux 7.0.1 baseline | Read-only diagnostics observed `ConnectionStateReady` on an authorized profile. This observation used the older installed executable. |

### Local Linux x64 8.0.0 candidate

The package built from `caea7e32d069d867c15e172616e684dc2cc4279d` measured 388,878,095 bytes. Its SHA-256 was `778d1d44b8b2acc089862a7f20aec20890b8eca33e1c572652a86f4cd35d3114`. All five packaged CLI tests passed, including the verified temporary installation via loopback. Modern and legacy MCP stdio and 10 isolated client configurations passed. Embedded transcription passed for WAV, OGG, MP3 and MP4, language detection, plain output, missing input and no audio. This package predates the Linux MCP registration supervision change.

### Real Claude Code native registration

Claude Code 2.1.319 ran in a temporary Linux profile with network access disabled. The candidate registered the MCP server through Claude's actual CLI. It preserved an unrelated saved entry and its original backup, passed an idempotent retry, and Claude reported the launcher Connected. A process trace confirmed that Claude started the candidate through the saved argument vector and preserved a session path with spaces as one argument. A separate profile exercised the configuration-verification failure when Claude removed an unrelated setting, with the exact prior backup preserved. This checks actual registration and stdio startup for Claude Code. Authorized Telegram data, GUI clients, and native launch in Codex and Copilot remain unverified.

## Hosted and publication gates

The preliminary hosted dispatch tested `caea7e3`. It failed overall after a Windows asset extraction error. [Hosted package run](https://github.com/podkolzzzin/tgcli/actions/runs/44811659310)

| Preliminary platform | Observed result |
| --- | --- |
| Linux x64 | Architecture and build passed. 24 Python tests passed. .NET reported 458 passed and 3 skipped. Packaged CLI/installer, transcription, modern and legacy MCP, and 10 isolated client profiles passed. |
| Intel macOS | Architecture and build passed. 24 Python tests passed. .NET reported 454 passed and 7 skipped. Packaged CLI/installer, transcription, modern and legacy MCP, and 11 isolated client profiles passed. |
| Windows x64 | Architecture, Python tools and build passed. .NET reported 449 passed, 11 skipped and one failure. `ConcurrentAssetExtractionKeepsOnlyVerifiedBytes` hit `UnauthorizedAccessException` during concurrent replacement of a corrupt asset. The packaged CLI, inherited ACL, transcription, MCP and upload steps did not run. |

| Gate | Status |
| --- | --- |
| Final release source and artifacts | Pending final verification. Record the exact source commit, Release-mode suite results, all three hosted package jobs, and exact byte counts and SHA-256 values. Linux and Intel macOS results above belong to the preliminary commit. Windows must complete its full package checks on corrected source. |
| Draft asset readback and publication | Pending tag/source match, notes and checksum verification against downloaded draft assets, and a public release URL. |
| Actual installation | Pending verified update at the intended runtime path, installed version check, and verification of the selected installed skill update. The candidate's loopback install used a temporary path. |
| Live Telegram acceptance | The replay guard passed offline. The first live fixture stopped during setup and was cleaned up. Candidate runs remain pending. |

The [package workflow](../../.github/workflows/package.yml) uses the [shared build recipe](../../tools/build_release.py). The [release workflow](../../.github/workflows/release.yml) waits for its three required package jobs, then checks the tag, notes, checksums and downloaded draft assets. The [installer](../../tools/install.py) verifies a candidate before replacing the executable. The x64 package matrix does not establish ARM support.

A controlled Linux x64 package comparison measured approximately 388 MB uncompressed and 231 MB compressed. The uncompressed and compressed cold `--version` launches took about 0.11 and 1.47 seconds, with peak RSS about 53 and 460 MiB respectively. The comparison used identical pre-integration source, runtime and assets, without opening Telegram. The release recipe selects one uncompressed bundle.

Linux external transcription and installer supervision cover descendants in the owned process group. Explicitly detached descendants fall outside that cleanup. On macOS and Windows, the external transcriber must wait for its children, and native registration requires a launcher that completes writes before exiting. An unowned descendant that keeps a pipe open can cause an uncertain cleanup result. The Linux registration stress probe used fixture executables, separate from the earlier real Claude Code registration run.

## Live Telegram acceptance

The first offline session scan refused the input before native startup. The corrected replay scanner passed 27 synthetic checks and the second authorized offline check of the exact session. Both scans finished before Telegram startup.

The first live setup created one private channel for the fixture. The initial creator-metadata guard then stopped setup before any synthetic send or second-client startup. The guarded finalizer later confirmed the exact fixture identity, membership and contents, and TDLib confirmed cleanup. There are zero fixture sends. The candidate's read, export, MCP and download checks did not run. The setup guard needs a new attempt before those checks can produce evidence.

| Scenario | Required observation | Status |
| --- | --- | --- |
| A01, first request after a new marker | With a controlled second client, obtain the marker within the requested first v8 CLI and MCP scope or see a classified failure. Document the actual cache conditions. | Pending |
| A03, network interruption and recovery | Observe bounded failure or explicit partial output during loss, cache-only behavior when requested, and successful reconnection. | Pending |
| A06–A09, scope, counters, edits and deletes | Verify same-scope repeated snapshots after controlled edits/deletes and preservation on local reads. Migration and live counter checks require additional fixtures. | Pending |
| A11–A13, contention, revocation and bots | Verify the classified outcomes on controlled native contention, revocation and disposable bot interactions. | Pending |
| A17, file references and citation | Verify an exact fixture attachment download. Reference turnover and opening links on relevant clients require additional checks. | Pending |

TDLib update-state provenance describes the state available to the library. An edit or new message can still be unknown to TDLib. Tombstones describe inferred absence within a verified accessible scope.
