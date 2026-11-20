# tgcli MCP installer/discovery audit

Read-only audit of current dirty 7.0.1 working tree. No repository source edits, real client configuration writes, installer executions against real profiles, or Telegram/account activity. Parent owns full test run. Reproductions compiled exact current `McpInstaller.cs`, `McpConfiguration.cs`, and `McpClientDiscovery.cs` into an external temporary project and used temporary synthetic profiles only.

## Coverage

Read all of `McpInstaller.cs`, `McpConfiguration.cs`, `McpClientDiscovery.cs`, `McpCommands.cs`, all 15 installer test methods (including their parameterized cases) in `TgCli.Tests/McpInstallerTests.cs`, the isolated installer E2E implementation `tools/e2e_mcp.py`, and installer claims/limitations in `MCP.md` / `MCP_PLAN.md`.

Useful existing defenses: shell-free native launch argument lists; special Windows `.cmd` restrictions; closed child stdin; bounded 30-second native registration window; best-effort process-tree kill on cancellation; dry-run does not launch clients or create directories; native post-write verification; private Unix temp/backup files; JSON duplicate-key rejection; JSONC token-offset insertion preserving unrelated text; TOML append with parser validation; conflicting registrations fail; canonicalized symlink targets; independent installer targets continue after errors. The current documentation correctly distinguishes saved registration from real application interoperability, and explicitly keeps Desktop/IDE live acceptance open.

## Confirmed findings

### I1 — P2: malformed Eclipse discovery registry aborts the entire Copilot family

- Trigger: a detectable valid Copilot CLI profile (e.g. `$HOME/.copilot/`) plus `$HOME/.eclipse/org.eclipse.ui.ide.prefs` containing duplicate `RECENT_WORKSPACES` keys, an unsupported continued property line, or a malformed Unicode escape.
- Expected: report the Eclipse target as failed, continue validating/installing independent Copilot CLI/IDE targets, as the per-target failure contract promises.
- Actual: `McpClientDiscovery.Find("copilot", ...)` throws `InvalidDataException` before returning any targets; `McpCommands.Install` has not called the installer yet. All family targets are skipped.
- Root cause: the wrapper at `McpClientDiscovery.cs:86-93` catches `IOException`, `UnauthorizedAccessException`, `JsonException`, and `InvalidOperationException`, but `InvalidDataException` derives directly from `SystemException`, not `IOException`. `AddEclipse` calls `JavaProperties.Get` at `McpClientDiscovery.cs:147`; duplicate / continuation / bad escape errors originate at `McpConfiguration.cs:182`, `209-210`, and `225`. `McpCommands.cs:15-17` performs discovery before installation.
- Reproduced output from exact-source temp harness: `discovery exception=InvalidDataException base=SystemException message=Duplicate Eclipse preference: RECENT_WORKSPACES.`
- Test gap: `BadProfileRegistryDoesNotBlockOtherCopilotTargets` (`TgCli.Tests/McpInstallerTests.cs:188-200`) covers malformed VS Code JSON registry only, whose exception is caught; there is no equivalent Eclipse registry case.
- Minimal plan: include expected Java-properties parse failures in per-adapter discovery isolation; regression fixture with valid Copilot target + malformed Eclipse registry must return a failed Eclipse target and leave valid target actionable.

### I2 — P2: valid Java-properties whitespace separators bypass existing-entry conflict protection

- Trigger: existing Eclipse prefs with a valid whitespace-delimited property, e.g. `mcp {"servers":{"tgcli":{"command":"existing","args":[]}}}`. Java `Properties.load` accepts unescaped whitespace as a key/value separator (official spec: https://docs.oracle.com/en/java/javase/21/docs/api/java.base/java/util/Properties.html#load(java.io.Reader)).
- Expected: recognize the existing MCP JSON and reject its conflicting `tgcli` entry without modifying the property.
- Actual: `ReadEntry` returns null; `Merge` appends a new `mcp=...` property with the requested command. The original text survives, but there are now two logical `mcp` properties and the later one supersedes the existing MCP settings when loaded by Java. Other servers inside the old `mcp` value can become unavailable, not just the conflicting tgcli registration. The installer's own re-read verifies only its appended property, so it reports success.
- Root cause: `JavaProperties.Lines` recognizes only `=` and `:` separators (`McpConfiguration.cs:200-208`). In the example it finds the JSON's first `:` and treats the prefix `mcp {"servers"` as the property key; it never identifies `mcp`. `Set` at `McpConfiguration.cs:185-191` then appends. The parser also supports only LF/CRLF natural line endings, whereas Java accepts CR; that is the same compatibility gap, not a separately reproduced finding.
- Reproduced output: `whitespace property original recognized=False`; `merged contains original=True new-entry=True`; emitted merged text contains original `mcp {...}` plus new `mcp=...`.
- Test gap: the only properties roundtrip test (`TgCli.Tests/McpInstallerTests.cs:57-67`) and E2E reader (`tools/e2e_mcp.py:178-180`) assume `mcp=`. The E2E parser repeats the same format assumption and cannot catch this discrepancy.
- Minimal plan: implement enough Java Properties syntax to identify a logical key reliably, including whitespace delimiters and natural line terminators, or conservatively refuse unsupported syntax instead of treating it as absent. Validate conflict-preservation and unrelated-server preservation using a reference Java-properties parser/fixtures.

### I3 — P3: Unix launcher discovery selects non-executable files ahead of a usable client

- Trigger: PATH begins with a directory containing a regular non-executable `codex`/`claude`/`copilot` file, followed by a directory containing a usable executable of the same name.
- Expected: pick the executable launcher matching normal command lookup, or use the direct config adapter if no executable exists.
- Actual: discovery picks the first non-executable file; native process creation fails with permission denied, so configuration is reported failed even though a usable client exists later in PATH.
- Root cause: `McpInstallEnvironment.Executable` (`McpClientDiscovery.cs:28-35`) checks only `File.Exists`, not executability on Unix.
- Reproduced exact-source output: `path picks non-executable=True` for first file mode 0600 and second file mode 0700.
- Test gap: the Windows PATH test at `TgCli.Tests/McpInstallerTests.cs:214-224` deliberately writes a plain test file and tests only path discovery; there is no Unix executable-permission case.
- Minimal plan: respect Unix executable accessibility during PATH lookup and test an unusable first match followed by a valid launcher.

## Risks / limitations, not new confirmed defects

- File-adapter optimistic compare-before-rename at `McpConfiguration.cs:153-159` has a residual TOCTOU window: another writer can change the file after validation and before rename. `MCP.md:60` already acknowledges this and recommends closing editors, so do not present it as a newly discovered promise violation. Stronger coordination would require supported client locking/atomic conditional update, not merely another check.
- Native registration runs with the client's own write semantics. Backup creation and exact command/arguments verification do not prove that unrelated settings survived a buggy or concurrently running client; this is documented and needs client-specific compatibility validation.
- `McpLaunch.Matches` checks command, args, URL absence and local/stdio type, but not enabled/disabled flags, allowed tools, environment overrides, or live health (`McpConfiguration.cs:33-37`). An `already-installed` result deliberately means matching saved launch, not enabled or callable integration. Keep that limitation in acceptance criteria rather than silently expanding installer authority over user preferences.
- Real client version/OS compatibility remains incomplete per `MCP.md:112-116` and `MCP_PLAN.md:140-146`. Fake-client E2E confirms our own expected CLI syntax and file shapes, not that current Desktop/IDE versions consume them. No real client app was invoked in this audit.
- `.cmd` quoting safety is source-reviewed only; Windows/macOS behavior and process-tree cancellation were not executed here.

## CLI/MCP parity and simplification

- Installation is intentionally CLI-only (`tgcli mcp install ...`); no MCP tool grants a model authority to reconfigure client applications. This is appropriate surface separation, not missing parity.
- Each family resolves the same `McpLaunch.Current` with an absolute executable/DLL and absolute explicit session; both file and native adapters verify with the same saved-entry predicate. File adapter status and native status have the same verified-command guarantee.
- `McpLaunch.ToJson(bool copilot = false)` has an unused parameter (`McpConfiguration.cs:27`); Copilot tools are added in `Merge` instead (`:110`). Remove unused boolean when doing authorized cleanup.
- `BuildArguments` includes a VS Code CLI path (`McpInstaller.cs:133-137`), yet `AddVsCode` never supplies a `Cli` or `Profile` to its targets (`McpClientDiscovery.cs:109-111,133`); the `Profile` field and this branch are unused by actual discovery. Either remove them or wire them only if supported/necessary, rather than maintaining unexercised alternate paths.
- Native adapter selection is based on display-name string prefixes/equality (`McpInstaller.cs:72,133,138`) and generic fallback for everything else. A small explicit adapter kind would separate labels from behavior; this becomes useful if more clients are added, but is not justification for a broad refactor now.
- Properties parsing should be one independently verified format boundary; the Python E2E reader should not duplicate its assumptions. The current mismatch in I2 shows why a reference parser or format corpus matters more than additional roundtrip tests with the same constrained writer.

## Reproduction artifacts

`/tmp/tgcli-installer-audit-probe/probe.csproj` links the exact current source. `/tmp/tgcli-installer-audit-probe/Program.cs` creates/deletes synthetic temporary fixtures and prints the three observations above. Run: `dotnet run --project /tmp/tgcli-installer-audit-probe/probe.csproj --verbosity quiet`. Execution passed (exit 0), with only platform analyzer warnings from Unix fixture permission setup. This is an audit probe, not a checked-in regression suite.
