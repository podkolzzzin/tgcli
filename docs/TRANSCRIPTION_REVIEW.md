# Transcription implementation review — 2026-10-28

Implemented v6.4.0 with `transcribe --input`, `message transcribe` and `chat export --transcribe` for voice notes, video notes, audio and video. Multilingual Whisper base weights, CPU libraries, decoder and license notices are embedded in the executable. .NET owns in-process model inference, cancellation and disposal. Captions remain intact; generated results include language and timestamped segments.

## Verified on Linux x64

- Real dependency restore and checksum-verified model/decoder preparation completed.
- All 102 .NET tests and all 4 Python asset-tool tests passed.
- Self-contained single-file publication with native extraction, ReadyToRun and compression succeeded (approximately 221 MiB).
- Published executable transcribed real WAV, OGG/Opus, MP3 and MP4 speech with an empty PATH and an initially absent user cache directory.
- Automatic language detection, plain output, missing input and video without an audio track passed the end-to-end checks.
- MCP protocol compatibility and isolated client installation passed (10 configurations).
- NuGet audit reported no vulnerable packages from the configured sources.
- Independent review verified the Whisper 1.9.1 API and native package layout. First-run cache handling was fixed after the real binary test exposed a missing-directory case.

## Local installation

- Installed `~/.local/bin/tgcli --version`: **6.4.0**. Installed bytes match the verified artifact at `artifacts/linux-x64/tgcli`.
- The installed executable successfully ran embedded Whisper transcription on the speech fixture.
- Previous local 6.3.0 executable is preserved at `artifacts/local-backup/tgcli-6.3.0`.
- Both `.codex/skills/tgcli/SKILL.md` and `.claude/skills/tgcli/SKILL.md` are updated in place.
- Earlier network and protected-directory blockers are resolved.

Windows and macOS checks are configured in CI; they were not run on this Linux host. No live Telegram-account media was used for verification. The task remains for user review and has not been marked accepted/done.

## 7.0.0 release preparation — 2026-11-03

- The previously uncommitted transcription implementation is being released as 7.0.0 at the owner's request. The 6.4.0 evidence above describes the earlier local build.
- Application and TDLib client versions, download instructions, changelog and both project skills now target 7.0.0.
- Release validation: all 102 .NET tests passed with no skips; all 4 Python asset-tool tests passed; both skill manifests validated.
