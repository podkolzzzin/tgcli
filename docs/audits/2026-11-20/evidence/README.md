# Evidence retained for the audit

All Telegram/message/session inputs in probes are synthetic. No account credentials or private correspondence are included. The existing incident report remains outside this repository.

- `baseline.json`: starting source SHA, locally installed executable hash/version, observed public latest release and hashes of all preexisting dirty files.
- `dotnet-test.log`: 113 passing tests, zero skipped/failed. Full TRX from this run remains in `/tmp/tgcli-audit-2026-11-20/test-results/audit.trx` (not required to read the audit).
- `python-tests.log`: four asset-preparation tests.
- `mcp-e2e.log`: installed Linux binary, temporary fake client profiles, no live authentication.
- `transcription-e2e.log`: installed Linux binary, public synthetic-test speech fixture, no Telegram audio.
- `help.txt`: 40 installed-binary help/version invocations covering all 31 command surfaces.
- `cli-probes.jsonl`: argv, exit code, stdout/stderr of safe parser/pre-session probes.
- `read-probe/`, `export-probe/`: compiled-current-helper observations; export output explicitly labels literal `SOURCE_REPLAY` separately from `ACTUAL_HELPER`.
- `session-probe/`, `installer-probe/`: exact-source temporary projects with fabricated filesystem fixtures. Root repeated these after collecting reports, confirming their output.

Probe C# and project files use `.txt` suffixes here so the product project's default recursive C# glob cannot accidentally compile audit harnesses. They are evidence, not new production tests. To rerun: build the current project with `dotnet test TgCli.Tests/TgCli.Tests.csproj`, copy a probe directory into a fresh temporary directory, remove the final `.txt` from `*.cs.txt` and `*.csproj.txt`, and run `dotnet run --project <copied-project> --verbosity quiet`. Project references use the audit machine's absolute checkout path; replace `/home/andrii/RiderProjects/tgcli` with your checkout path when running elsewhere. Keep all generated binaries and fixtures outside the repository. These projects require .NET 10; some reference assemblies from `bin/Debug/net10.0` produced by the baseline build.

The prose reports record additional protocol requests and static code reasoning. No claim is made that these probes establish live Telegram freshness, cross-platform success, or complete command-handler integration coverage. Synthetic output token-shaped strings are parser fixtures, not credentials.
