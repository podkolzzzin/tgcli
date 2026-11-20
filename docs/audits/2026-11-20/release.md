# tgcli release, platform and CI subaudit

Read-only source review on 2026-11-20. No network, build, test, publish, installer, or Telegram actions were performed by this subagent. The checkout already contains uncommitted 7.0.1 work, so findings describe the working tree; they do not establish what an existing GitHub release contains. Paths below are relative to `/home/andrii/RiderProjects/tgcli`.

## Concrete findings

### R1 — Installation instructions install 7.0.0 while introducing 7.0.1 (P2)

`README.md:7` advertises the 7.0.1 startup-readiness and short-page fixes, and `TgCli.csproj:6` is version 7.0.1. All three copyable install commands still download v7.0.0 (`README.md:32`, `:38`, `:44`). `CHANGELOG.md:3` begins at 7.0.0, with no 7.0.1 entry. A reader following the documented install does not obtain the fixes described directly above it. `RELEASE.md:5-6` explicitly requires current-version release notes/install commands. Parent audit independently verified that the public latest release remains v7.0.0 (published 2026-11-03), with no public 7.0.1. Therefore the download URLs correctly match the current public release; the mismatch is that the working README announces locally installed/unreleased 7.0.1 behavior without labeling it unreleased. Treat this as release-preparation/documentation state, not as evidence of a broken public 7.0.1 release.

### R2 — Unix install one-liners can replace a working executable with an HTTP error response or partial download (P2)

`README.md:32` and `README.md:44` execute `sudo curl -L <url> -o /usr/local/bin/tgcli && sudo chmod +x /usr/local/bin/tgcli`. They neither fail on HTTP errors (`-f`) nor stage the download before replacing the destination. Thus an HTTP error body may overwrite the executable while curl reports a successful transfer, and a failed/interrupted transfer can truncate the existing installation. The same pattern appears in `CHANGELOG.md:14,22` and historical install blocks. Recommended correction: fail HTTP errors and download into a temporary file, verify, then atomically install; preserve the current executable until the replacement is ready. No failure was induced during this audit.

### R3 — MCP quick-start source build omits now-mandatory asset preparation (P2)

`MCP.md:6-7` starts directly with `dotnet publish`. In a clean checkout that fails because `TgCli.csproj:27-29` requires `.transcription/model.bin` and a RID-specific `manifest.json` before `PrepareForPublish`. `.transcription` is ignored (`.gitignore:22-23`). The correct restore + preparation sequence exists at `README.md:204-214`, but the independently copyable MCP recipe does not include or link it. `MCP.md:3` also still identifies tgcli 6.3.0. This is a static, deterministic precondition mismatch; no publish was run.

### R4 — Automated release publication does not carry out the written release-note contract (P2)

`RELEASE.md:3-7` requires CHANGELOG-based release notes, current platform install commands, and copyability verification. `.github/workflows/release.yml:104-109` only provides `draft: true` and `files` to the GitHub release action; it does not pass `body`, `body_path`, or extract CHANGELOG content. `.github/workflows/release.yml:111-122` publishes that draft after the matrix succeeds without any note/version validation. For a freshly created release, the workflow itself therefore does not populate the required release body. A manually pre-created/edited draft could supply it, but no such manual state was inspected. Also no comparison between the tag name and project `Version` is configured.

### R5 — Published-binary E2E does not cover the general Telegram CLI surface (coverage gap, P2)

The workflow step label `Test CLI and MCP` (`release.yml:58`) runs mostly helper/unit tests and MCP fake-backend protocol tests. The only executable E2E scripts invoked are transcription (`release.yml:77-79`) and MCP (`:81-83`; PR/main equivalents `e2e.yml:70-76`). Source search finds no `Program.Main`, command-parser `--help`/`--version`, or FriendlyErrorFilter tests under `TgCli.Tests`. Beyond isolated `mcp`, `mcp install`, and local `transcribe`, the scripts do not invoke the application command handlers for login, chat reads/search/context/stats/export, channel metrics/comments, forum topics, message get/transcribe, downloads, bots, or session secret/status/unlock workflows. Existing helper tests do cover portions of these features, so this is an integration/contract coverage gap rather than proof those features fail. Particularly absent are regression gates for option binding/defaults/required arguments, stream routing, exit codes, end-to-end output schemas, and invalid-argument side effects across that full command surface.

## Release/build inventory

| Area | Implemented behavior | Evidence |
| --- | --- | --- |
| Framework/package | .NET 10, executable `tgcli`, invariant globalization, version 7.0.1 | `TgCli.csproj:3-10` |
| Release targets | `linux-x64` on ubuntu-latest; `osx-x64` on macos-latest; `win-x64` on windows-latest | `release.yml:18-28`; `e2e.yml:19-29` |
| Trigger | Tag `v*` and manual release workflow; PR, main push, manual E2E workflow | `release.yml:3-7`; `e2e.yml:3-8` |
| Release permissions | contents write for release workflow; contents read for E2E | `release.yml:9-10`; `e2e.yml:10-11` |
| Build assets | Whisper multilingual base model pinned to revision + SHA256; imageio FFmpeg wheels pinned + SHA256 for three x64 RIDs; native runtime copied from exact Whisper.net 1.9.1 NuGet layout | `tools/prepare_transcription.py:14-26,35-48,69-89` |
| Preparation output | Recreates per-RID directory using staging, copies licenses and emits hash manifest | `tools/prepare_transcription.py:92-118` |
| Resource embedding | Optional model in normal builds; per-RID assets embedded; publish requires model and manifest | `TgCli.csproj:18-29` |
| Release flags | Self-contained single file, native extraction, ReadyToRun, compression, stripped symbols | `release.yml:64-75` |
| PR/main flags | Self-contained single file with native extraction; does not use the release-only ReadyToRun/compression flags | `e2e.yml:62-68` |
| Test gates | Four Python asset-tool tests; .NET test project; published local transcription E2E; published MCP E2E | `release.yml:54-83`; `e2e.yml:52-76` |
| Artifact | One extensionless binary for Unix or one `.exe` for Windows; uploaded to workflow artifacts and draft release | `release.yml:85-109` |
| Final publish | Waits for all platform publish jobs before setting draft false/latest | `release.yml:111-122` |
| Package audit | `dotnet list ... package --vulnerable --include-transitive` in release only, no explicit result parsing/policy | `release.yml:47-48` |

No ARM64, musl, or 32-bit release target or decoder asset is configured. `macos-latest` is not an architecture-pinned runner label; source alone does not establish which host architecture or translation layer is used in an actual hosted run. Do not infer native Apple Silicon support from the general "macOS" label. No workflow run history was fetched. The repository does not configure downloadable binary checksums, signing/notarization, or a release smoke check after the uploaded binary is retrieved; these are hardening/acceptance gaps, not observed runtime failures.

## Exact E2E scope

### Published local transcription (`tools/e2e_transcription.py`)

- Fetches pinned SHA256-verified JFK speech fixture at build/test time (`:13-14,29-30`).
- Copies only the deployed binary into a temporary standalone directory (`:32-38`).
- Creates WAV, OGG/Opus, MP3, MP4 and silent MP4 with the prepared decoder (`:39-46`).
- Clears PATH and redirects data/native-bundle extraction directories; uses real embedded inference (`:47-50`). This demonstrates external executables are not needed on PATH; network egress is not explicitly disabled.
- Asserts JSON generated/model markers, a very small English keyword check, and nonnegative ordered segment intervals for four media types (`:51-56`).
- Checks automatic English detection, plain output, missing media and silent video failures with no stdout (`:57-65`).
- Does not invoke Telegram `message transcribe`, export-integrated transcription, a non-English fixture, cancellation, or interrupted extraction through the published CLI. Some cancellation/extraction behavior is covered at unit level.

### Published MCP and installer E2E (`tools/e2e_mcp.py`, `tools/check_mcp.py`)

- Temporary Unicode/spaces home, fake launchers, isolated config/data/bundle directories; removes Telegram API credential environment variables (`e2e_mcp.py:225-246`; `check_mcp.py:73-82`).
- Modern protocol 2026-07-28 and one legacy protocol 2025-11-25; 12-tool listing, modern unsupported-version error, diagnostics authorization error with no credentials, catalog recovery, clean EOF/stderr (`check_mcp.py:19-66`). Other legacy versions are unit/protocol-test scope, not this E2E helper.
- Fake Claude/Codex/Copilot CLI registration plus representative Desktop/IDE files; verifies persisted absolute executable/session, unrelated settings backups, installer idempotency, and actual calls to fake native launchers (`e2e_mcp.py:21-73,140-152,191-222`).
- Checks 10 configs on Linux, 11 on macOS or Windows; Xcode fixture gated to darwin and Visual Studio fixture gated to Windows (`e2e_mcp.py:80-99,121-127,241`).
- Starts server from saved Claude Desktop entry (`e2e_mcp.py:248-250`).
- `check_mcp.py --native-install` can additionally use installed real CLIs, with skip for absent executables; it is not run in either CI workflow (`check_mcp.py:85-102,115-116`).
- Neither CI script logs into Telegram, exercises a real Telegram read/download, or launches actual Desktop/IDE apps. The docs explicitly keep those acceptance checks open (`MCP.md:109-117,128`; `MCP_PLAN.md:143`).

## Unit inventory and platform caveats

Static declaration counts, not executed/result counts (theories can expand into multiple cases):

| Test file | Facts | Theories |
| --- | ---: | ---: |
| ChatHistoryTests.cs | 6 | 0 |
| ExportTests.cs | 22 | 1 |
| McpInstallerTests.cs | 12 | 3 |
| McpProtocolTests.cs | 8 | 2 |
| McpSessionGateTests.cs | 4 | 0 |
| McpToolsTests.cs | 5 | 2 |
| TelegramConnectionTests.cs | 5 | 0 |
| TelegramSessionTests.cs | 3 | 0 |
| TranscriptionTests.cs | 11 | 1 |

- `TgCli.Tests/TgCli.Tests.csproj:8-12`: Microsoft.NET.Test.Sdk 18.0.1, xunit.v3 3.2.2, runner 3.1.5, project refs to production and fake MCP test server.
- Real decoder unit tests call `Decoder()`, which looks under current-working-directory `.transcription/<runtime RID>/`, then only `/usr/bin/ffmpeg`, otherwise calls `Assert.Skip` (`TgCli.Tests/TranscriptionTests.cs:152-159`). Thus a successful ordinary dotnet test run may include skips; preparation under a different cwd/host RID does not guarantee decoder coverage. The separate published-binary transcription E2E uses the explicit matrix RID and prepared path and has no skip for decoder absence.
- Unix mode assertion in installer tests runs only off Windows (`TgCli.Tests/McpInstallerTests.cs:77`).
- Python preparation tests cover checksum failure/cached download, safe wheel extraction, missing runtime and Linux/macOS runtime path mapping (`tools/test_prepare_transcription.py:11-58`); no Windows-specific synthetic runtime mapping test exists, although Windows CI preparation/E2E exercises the actual Windows package path.
- Historical local validation is explicitly Linux-only; `docs/TRANSCRIPTION_REVIEW.md:24` says Windows/macOS checks were configured but not run on that host. `MCP.md:117` still asks to observe successful hosted runs. Current platform success cannot be concluded solely from workflow definitions.

## Parent-supplied execution evidence

The parent separately reports 113 .NET tests and 4 Python tests passing, installed Linux MCP E2E passing for 10 config targets, and installed WAV/OGG/MP3/MP4 transcription checks passing. These were not rerun by this subagent. They support Linux behavior for the exercised paths, not unexecuted CLI handlers or hosted Windows/macOS outcomes.
