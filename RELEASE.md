# Release instructions

Update the project version and add the corresponding top entry in `CHANGELOG.md`. Keep the tagged install block in that entry identical to the block in `README.md`. Check the migration guide and release examples against the built executable's help.

[Build from source](README.md#build-from-source) describes the shared build used by pull requests, main, and the tag workflow. The package matrix requires x64 runners on Ubuntu 24.04, Intel macOS 15, and Windows Server 2025. Each runner executes unit and protocol tests, then exercises the final single file with the general CLI, installer, transcription, and MCP checks before uploading it as a workflow artifact. These checks use isolated homes and synthetic profiles. The packaged transcription checks exercise the embedded engine. Verify optional external executables, live Telegram, and client interoperability separately.

Create and push a `vX.Y.Z` tag for the reviewed commit. The release workflow waits for all three package jobs, checks the project version and install links against the tag, and generates `SHA256SUMS` and the release body from the tagged checkout. The final job attaches the three tested binaries and the manifest to one draft, downloads those assets, verifies the bytes and persisted release notes, and then publishes the draft. A failed verification leaves the draft unpublished. Inspect all three hosted job results for the tag and verify the published install as part of release acceptance.

## Package size and startup

The 7.0.1 baseline on Linux x64 measured the installed 361,105,352-byte binary with `--version`. A new native extraction directory took 0.461 seconds with peak RSS of 53,660 KiB. Three runs reusing that directory took 0.175–0.321 seconds and 26,216–26,584 KiB of peak RSS. This was a local startup probe without Telegram or inference.

Before embedding, Linux transcription uses a 147,951,465-byte multilingual model and 84,803,946 bytes of platform assets, including the 79,826,272-byte decoder and 4,935,024 bytes of Whisper runtime libraries. The installed 7.0.1 build flags were unverified.

A controlled comparison on Linux x64 built the same pre-integration 8.0.0 source and assets with .NET 10 and ReadyToRun. Single-file compression was the only changed flag. We interleaved `--version` calls on one host with separate native extraction directories. A cold run used a new extraction directory. A warm run reused it. The test did not flush the filesystem cache or open Telegram or the transcription engine.

| Single-file compression | Binary bytes | Cold wall time | Warm wall time, 3 runs | Warm peak RSS |
| --- | ---: | ---: | ---: | ---: |
| Enabled | 231,423,865 | 1.473 s | 1.086–1.109 s | 470,620–470,792 KiB |
| Disabled | 387,557,647 | 0.114 s | 0.048–0.050 s | 33,548–34,000 KiB |

Both builds extracted 47,264,800 bytes of native files. The release uses one uncompressed self-contained package with offline transcription. Disabling compression adds about 156 MB to the measured download and reduces startup time and memory for ordinary CLI invocations.
