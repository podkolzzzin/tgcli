# Embedded media transcription

Implement the requested offline speech-to-text for Telegram voice notes, video notes, audio and video, and local media files. The multilingual Whisper base model is embedded in the executable and inference runs in the tgcli process through Whisper.net, owned and disposed by .NET. Bundle FFmpeg for media decoding; no Python, external model, service, or system FFmpeg is required at runtime. Build-time asset downloads are pinned and checksum verified.

Approaches: an external transcription command already exists but does not satisfy embedded inference. Telegram premium transcription depends on account/server availability. Embedded Whisper meets the stated constraints; base is the implementation choice balancing binary size and CPU cost. FFmpeg runs as a bundled decoder child process; the model runs in-process.

Interfaces: `tgcli transcribe --input <file> [--language auto|uk|en] [--format json|plain]`; `tgcli message transcribe --chat-id ... --message-id ...`; `tgcli chat export --transcribe [--transcribe-language uk]`. Preserve opt-in operation, captions and the existing generated_text envelope, add model/language/timestamped segments. Keep external --transcribe-command compatible and reject ambiguous dual selection. Include transcripts in Markdown/plain/TSV exports as well as JSON. Transcribe only the primary attachment, never video thumbnails.

Cancellation must terminate decoding and propagate to inference. Decode to a temporary 16 kHz mono WAV with cleanup in finally. Reuse a lazy model across an export. Invalid input, unsupported attachment, no audio track, missing build assets and decoder failures produce actionable nonzero errors. Native logs/progress must stay off stdout.

Ship version 6.4.0, update both project skill copies and user docs, publish and verify the single-file binary, then update the installed local version. The task remains for user review, not accepted/done. Current restricted environment may prevent downloads, .codex edits and local installation; report these explicitly if not restored.
