# Changelog

## 4.0.1

- Fix bundled TDLib native library resolution for installed single-file launchers.
- Send command errors to stderr and show concise user-facing messages instead of stdout stack traces.
- Align lightweight JSON/JSONL rows from chat/message listing and search commands with snake_case v4 field names.
- Treat missing extensionless `download --output` paths as directories to avoid accidental files named like directory targets.
- Document download output and session-lock behavior.

## 4.0.0

- Introduce the versioned `tgcli.message/4.0` JSONL schema with stable sender identity, rich Telegram metadata, structured entities, complete attachment metadata, and resolved reply targets.
- Add chronological migration-aware exports and detailed completeness manifests.
- Add atomic incremental/resumable JSONL caches, resume tokens, field projections, and deletion tombstones.
- Add opt-in voice/video-note transcription hooks.
- Add machine-readable `diagnostics` and reply-aware `chat context`.
- Document TDLib, short-message, migration-source, reply, and permalink ID semantics.

The JSONL shape is intentionally incompatible with 3.x.
