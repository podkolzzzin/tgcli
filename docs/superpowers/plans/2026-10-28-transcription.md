# Embedded Transcription Implementation Plan

> **For agentic workers:** Use executing-plans to implement this plan task-by-task.

**Goal:** Offline transcription of Telegram notes/full media and local files with bundled model/runtime.
**Architecture:** A reusable media transcription service owns decoding, a lazy Whisper engine and structured results. Commands and export share it. Build tooling pins and embeds model, decoder and runtime assets.
**Tech Stack:** .NET 10, Whisper.net 1.9.1 CPU, multilingual ggml-base, bundled FFmpeg.
**Spec:** docs/superpowers/specs/2026-10-28-transcription-design.md

## Global Constraints

Inference in-process under .NET; model in executable; no runtime downloads. Opt-in transcription. Preserve existing export schema/captions. Version 6.4.0. Leave task for review.

## Review Focus

Unsupported attachments and thumbnails; missing audio tracks; cancellation and temp cleanup; stdout purity; standalone single-file resource loading.

### Task 1: Shared transcription service
- [x] Write tests for supported primary attachments, option validation, caption-preserving generated output, decoder errors/cancellation/cleanup.
- [x] Run tests and observe missing behavior.
- [x] Implement MediaTranscription.cs and embedded Whisper/decoder resource handling.
- [x] Run focused tests.

### Task 2: CLI and export
- [x] Test local/Telegram command input validation and output, and export transcript rendering.
- [x] Add local `transcribe`, `message transcribe`, and `chat export --transcribe` with language selection and reusable service.
- [x] Run full .NET and MCP protocol/E2E tests.

### Task 3: Packaging, docs and local update
- [x] Add pinned checksum-verified build asset preparation and resource embedding, including licenses.
- [x] Bump version/TDLib app version; update README, changelog and both skill copies.
- [x] Publish single-file release and verify real audio, video, voice and video-note equivalents offline plus error paths.
- [x] Review changes, update local binary atomically, verify installed version; report any permission/network blockers.


## Validation ledger (2026-10-28)

- Existing source baseline: 84 .NET tests passed before implementation.
- Shared service first pass: 11 focused .NET tests passed before the external dependency was added.
- Asset preparation: 4 Python tests pass, including a regression that failed for the actual NuGet `build/<rid>` layout before the fix.
- Native review: API signatures checked against official Whisper.net tag 1.9.1; independent reviewer found no further important issues after the packaging/cancellation fixes.
- Managed-only validation: a temporary copy in `/tmp/tgcli-transcription-managed-check` replaces only the unavailable Whisper engine with a throwing implementation, removes its three unavailable package references, and exercises the actual command/export/service/resource code (102 tests passed, including real FFmpeg normalization and cancellation). This is explicitly not a production build or a native inference test. It must never be installed as tgcli.
- Root project restore is blocked: Whisper.net, Whisper.net.Runtime and Whisper.net.Runtime.NoAvx 1.9.1 are absent locally, and DNS/network access to api.nuget.org is denied. Thus native loading, prepared assets, publish and real speech E2E remain unverified.
- Codex skill write was rejected by approval settings; its full proposed content is in `docs/tgcli-codex-SKILL.md`. Claude skill is updated in place.
- Installed `/home/andrii/.local/bin/tgcli --version` still prints 6.3.0. Source metadata and TDLib application version are 6.4.0. No replacement binary has been installed.
- No release/push/acceptance action performed. JungleManager task is not user-accepted.


## Completion after access was restored

- Restored actual Whisper packages and prepared embedded assets with verified checksums.
- Real project: 102 .NET tests passed; asset tooling: 4 tests passed; no vulnerable NuGet packages reported.
- Published-binary speech E2E passed WAV, OGG, MP3 and MP4, automatic language detection, plain output and error cases with an empty PATH.
- Fixed a first-run bug exposed by E2E: request creation of LocalApplicationData instead of accepting an empty path when its directory does not exist.
- Published MCP protocol and 10 isolated client installation configurations passed.
- Both project skills are now updated. Installed local binary is 6.4.0 and matches the tested artifact; old 6.3.0 binary is backed up under artifacts/local-backup.
- Historical blocked-run notes above are superseded by this completion evidence. No user acceptance/done state was set.
