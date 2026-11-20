# tgcli 8 release validation

Release 8.0.0 is published and installed locally. This record separates final release verification from earlier candidate and controlled live evidence. See the [remediation matrix](v8-remediation-coverage.md) for named audit and historical-flow checks and the [migration guide](../v8-migration.md) for script changes.

## Integrated source and local execution

| Scope | Observed result |
| --- | --- |
| Integrated Linux Debug .NET suite at `caea7e3` | 461 passed, 0 failed, 0 skipped. An intermediate 437/438 run exposed a held-lock test that expected empty stdout after diagnostics began returning a structured report. The passing integrated suite separately covers the ordinary lock timeout and the diagnostic timeout with structured lock evidence. Its synthetic native tests also cover cancellation during enrichment, transitional authorization, exact topic identity and the unknown participant-count sentinel. |
| Independent Linux review of `caea7e3` | 159 focused cases passed across CLI wire, synthetic native handlers and MCP. Raw stdio remained usable after malformed metadata, duplicate keys and malformed Unicode. Independent TOML parsing verified exact supplementary Unicode paths, and inaccessible Eclipse traversal preserved a readable sibling. |
| Integrated Linux Debug .NET suite at `5f5514a` | 466 passed, 0 failed, 0 skipped after Linux native MCP registration supervision. The installer change also passed 169 focused lane tests, 49 independent focused tests, modern and legacy MCP on a self-contained package with 10 fixture configurations, and 12 packaged deadline stress trials with zero late writes. |
| Linux Release .NET suite at `cd1d2f7` | 467 passed, 0 failed, 0 skipped with `dotnet test TgCli.Tests/TgCli.Tests.csproj -c Release --no-restore`. |
| Linux Release .NET suite at `3cfd675` | 473 passed, 0 failed, 0 skipped after the diagnostic deadline fix. The host owns diagnostic publication; a late handler cannot print a second record. The deterministic outer-deadline test retains observed lock evidence, and cancellation, help and invalid input keep their separate contracts. |
| Integrated Python tools on Linux | 24 passed. The installer preserved the previous executable through rejected candidates. Release checks covered mismatched source version and notes, missing RIDs and changed draft files. |
| Skills | All three tracked references match. Independent SOL evaluations with the same synthetic scenario found that the v7 reference led to retired flags, schema 5 and integer topic IDs. The v8 reference led to a verified refresh, scoped checkpoint delta, string IDs, continuation after an empty page and caution after an uncertain mutation. The packaged CLI exercised 31 scenarios in empty synthetic profiles and the MCP probe verified its catalog and validation. |
| Linux media fault injection | Actual SIGKILL during a staged batch copy preserved old final files and a restart reused the verified complete file. SIGINT during embedded inference returned 130 with temporary WAV cleanup. The Linux packaged external transcriber terminated a pipe-holding child after its launcher exited, preserving an unrelated process. |
| Installed Linux 7.0.1 baseline | Read-only diagnostics observed `ConnectionStateReady` on an authorized profile. This observation used the older installed executable. |

### Local Linux x64 8.0.0 candidate

The Linux x64 package built from `cd1d2f778d5f377db47f7ed06532d67288eea854` measured 388,886,799 bytes. Its SHA-256 was `79d825ea947e74df8a9b8b485aaa3d1b761ad6d0201325cbe1978fa0b9b79688`. It reported `8.0.0`. All five packaged CLI tests passed, including the verified temporary installation via loopback. Modern and legacy MCP stdio and 10 isolated client configurations passed. Embedded transcription passed WAV, OGG, MP3 and MP4, language detection, plain output, missing input and no-audio cases. The same package passed 31 CLI skill scenarios and the MCP skill catalog and validation probe.

### Real Claude Code native registration

Claude Code 2.1.319 ran in a temporary Linux profile with network access disabled. The exact local release candidate invoked the real Claude installer through the Linux process supervisor and returned `installed` in 0.579 seconds. It preserved an unrelated saved entry and the exact original backup. A repeat returned `already-installed`. Claude reported the launcher Connected, and the process trace confirmed it passed the saved session path with spaces as one argument. A prior isolated profile exercised the configuration-verification failure when Claude removed an unrelated setting, with the exact prior backup preserved. GUI clients and native launch in Codex and Copilot remain unverified.

## Hosted and publication gates

The first preliminary dispatch passed on Linux and Intel macOS. On Windows, `ConcurrentAssetExtractionKeepsOnlyVerifiedBytes` failed with a sharing error during replacement. The extractor now serializes verification and publication by destination. The Windows .NET suite passed on the next run. [First hosted dispatch](https://github.com/podkolzzzin/tgcli/actions/runs/44811659310)

The second preliminary dispatch used `cd1d2f7`. Each job passed architecture checks, Python tooling and the release build. [Second hosted dispatch](https://github.com/podkolzzzin/tgcli/actions/runs/44813411067)

| Preliminary platform | Observed result |
| --- | --- |
| Linux x64 | .NET reported 464 passed and 3 skipped. All five packaged CLI and verified installer cases, embedded transcription, modern and legacy MCP, and 10 isolated client profiles passed. |
| Intel macOS x64 | .NET reported 456 passed and 11 skipped. All five packaged CLI and verified installer cases, embedded transcription, modern and legacy MCP, and 11 isolated client profiles passed. |
| Windows x64 | .NET reported 452 passed and 15 skipped, including a pass for the extraction regression. The installer fixture verified the candidate bytes and removal of staging files. The packaged CLI gate then stopped at the test's PowerShell ACL query. The packaged transcription, MCP and upload steps did not run. |

The Windows ACL diagnostic reproduced the PowerShell 5 security module's autoload failure on the original untouched sentinel and a synthetic replacement. `icacls` confirmed inheritance of the Authenticated Users read/execute entry on the synthetic replacement. The package test correction uses the native `icacls /save` check for an inherited allow entry by SID. The corrected query passed a hosted Windows probe. The probe did not replace the complete package matrix. [PowerShell diagnostic](https://github.com/podkolzzzin/tgcli/actions/runs/44814293657), [native ACL query probe](https://github.com/podkolzzzin/tgcli/actions/runs/44814626274)

The third dispatch passed every packaged gate on Linux and Windows, including the Windows inherited ACL check. Intel macOS exposed a diagnostic publication race: exit 5 could arrive without its JSON record when both the inner and outer deadlines expired before their continuations ran. Suspending a Linux subprocess reproduced the failure in all three trials. The fix removes the competing diagnostic timer and makes the CLI host the sole output owner. Independent suspended-process trials then returned exactly one observed-lock timeout record and exit 5 in all three cases; SIGINT retained empty stdout and exit 130. The fourth matrix below verified this fix. [Third hosted dispatch](https://github.com/podkolzzzin/tgcli/actions/runs/44815074699)

| Gate | Status |
| --- | --- |
| Final release source and artifacts | Passed on tagged source `a77b16333ebd8ee0ae6626b4341cd2a88787f3ba`. The fourth pre-release matrix and the tagged matrix passed on all three operating systems. Artifact hashes and counts are recorded below. |
| Draft asset readback and publication | The single publisher downloaded all draft assets, verified notes, names and hashes, then published [v8.0.0](https://github.com/podkolzzzin/tgcli/releases/tag/v8.0.0) at `2026-11-20T21:27:45Z`. A separate download of all public assets also matched `SHA256SUMS`. |
| Actual installation | The verified installer updated `~/.local/bin/tgcli` to `8.0.0`; its bytes match the public Linux asset. A normal online chat-list request returned exit 0, one row, `source: telegram` and a fulfilled bound. All three tracked skill copies and the selected local workflow guidance were updated; 31 CLI examples and the MCP catalog checks passed against the installed binary. |
| Live Telegram acceptance | Controlled Linux fixture checks passed for first ordinary CLI read after a separate producer write, same-session edit/delete refresh, MCP paging and CLI parity, and a verified attachment download. Scope limits are below. |

### Final tagged verification

The [fourth pre-release matrix](https://github.com/podkolzzzin/tgcli/actions/runs/44817995577) and [tagged release workflow](https://github.com/podkolzzzin/tgcli/actions/runs/44819500591) both succeeded on the exact tagged commit. The tagged workflow produced these results; platform-specific skipped fixtures are counted explicitly.

| Platform | .NET passed / skipped | Python passed / skipped | Packaged checks |
| --- | ---: | ---: | --- |
| Linux x64 | 470 / 3 | 24 / 0 | Five CLI/installer cases; four transcription formats and edge cases; modern/legacy MCP; 10 isolated client profiles. |
| Intel macOS x64 | 462 / 11 | 24 / 0 | Five CLI/installer cases; four transcription formats and edge cases; modern/legacy MCP; 11 isolated client profiles. |
| Windows x64 | 458 / 15 | 22 / 2 | Five CLI/installer cases including inherited ACL; four transcription formats and edge cases; modern/legacy MCP; 11 isolated client profiles. |

All jobs reported zero failures. NuGet package audits reported no known vulnerable packages; this does not audit every embedded native component. The main checkout passed a fresh 473-test Linux Release run after its fast-forward, with zero failures or skips. Its 41 original changed files were preserved byte-for-byte in the baseline commit and additional backups before integration.

| Public asset | Bytes | Verified SHA-256 |
| --- | ---: | --- |
| `tgcli-linux-x64` | 389,077,109 | `adf2de58b1e104ed0b4f8e3b638a8580b2cf7e519e3ca637e625dd79ad5b5e2a` |
| `tgcli-osx-x64` | 361,437,094 | `8314bb5cdb903bede8bf412325ece03e75951adc4f98a517fecf284df09bccc5` |
| `tgcli-win-x64.exe` | 383,100,105 | `a5c68abe9e5187d189eab25a57587968ab0cd20f9faf5d33c70fec0f8d0b07f5` |

The installed Linux executable matches the Linux digest above. The initial installed diagnostic observed valid authorization with `ConnectionStateUpdating` and returned exit 1 with `connection_unavailable`; it did not claim readiness. The subsequent ordinary online read completed successfully without repair flags, authorization resets or manual cache operations. Diagnostics reports the observed connection state, while ordinary online reads wait for readiness. The working authorization was retained.

The final local package after the diagnostic fix also passed the suspended-process regression: the normal control and three delayed runs each produced one observed-lock JSON record with exit 5; SIGINT produced no stdout and exit 130. Live freshness and edit/delete evidence below was collected on the earlier `cd1d2f7` artifact. The later production change concerns diagnostic publication, and those live observations are not relabeled as tests of downloaded release bytes.

The [package workflow](../../.github/workflows/package.yml) uses the [shared build recipe](../../tools/build_release.py). The [release workflow](../../.github/workflows/release.yml) waits for its three required package jobs, then checks the tag, notes, checksums and downloaded draft assets. The [installer](../../tools/install.py) verifies a candidate before replacing the executable. The x64 package matrix does not establish ARM support.

A controlled Linux x64 package comparison measured approximately 388 MB uncompressed and 231 MB compressed. The uncompressed and compressed cold `--version` launches took about 0.11 and 1.47 seconds, with peak RSS about 53 and 460 MiB respectively. The comparison used identical pre-integration source, runtime and assets, without opening Telegram. The release recipe selects one uncompressed bundle.

Linux external transcription and installer supervision cover descendants in the owned process group. Explicitly detached descendants fall outside that cleanup. On macOS and Windows, the external transcriber must wait for its children, and native registration requires a launcher that completes writes before exiting. An unowned descendant that keeps a pipe open can cause an uncertain cleanup result. The Linux registration stress probe used fixture executables, separate from the earlier real Claude Code registration run.

## Live Telegram acceptance

The exact Linux package above ran against disposable private fixtures. An initial setup attempt stopped at the creator-metadata guard, before any synthetic sends. The native finalizer confirmed cleanup. After a bounded read-only metadata recheck was added to the fixture harness, the following scenarios ran.

| Scenario | Observed result |
| --- | --- |
| A01, first ordinary read after a separate producer write | The producer had no TDLib message database before native startup. After it posted a new marker and closed, the next ordinary consumer CLI read returned that marker and the previous warm marker with no repair flags. The consumer used its existing database. |
| A02, initial and resumed read | Exact latest two on the first fixture read and the warm read, then two by bounded continuation and six on the full read, including the native creation service row. JSON and JSONL preserved schema 8.0, decimal string IDs and UTC chronological ordering. |
| A08, initial and incremental snapshot | The complete initial export had six active rows and a verified manifest. In a separate original-session fixture, one append, edit and deletion produced six active rows and one tombstone on each of two complete refreshes. Both refreshes retained the exact expected scope and verified data digest. The second retained the checkpoint and generation. |
| FLOW-16, MCP stdio continuation and parity | Four pages returned six exact rows once. The first page matched the comparable CLI page on stable fields. A cursor with changed filters was rejected and the MCP child exited cleanly. |
| A17, attachment | A packaged CLI download matched all 54 bytes of the synthetic fixture document. |
| Fixture cleanup | The separate producer and original-session fixture runs each ended with a successful guarded native cleanup receipt. The temporary producer copy was removed. |

The separate producer's replay guard stopped its reopen before attempting an edit or deletion. The edit/delete, incremental export, MCP and download results came from the subsequent original-session-only run. An empty consumer database, separate-producer edit/delete, forced offline and reconnect conditions, live flood waits, revocation and bot mutations, migration, linked channel comments, current counters, expired attachment references and client link opens remain unverified. The tested document download and paginated MCP run used the same verified artifact as the separate-producer freshness check.

TDLib update-state provenance describes the state available to the library. An edit or new message can still be unknown to TDLib. Tombstones describe inferred absence within a verified accessible scope.
