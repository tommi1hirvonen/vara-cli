## 1. Foundation

- [ ] 1.1 Add a centrally-managed `Spectre.Console` package reference to `Vara.Cli` and verify `dotnet build` succeeds
- [ ] 1.2 Create a `Vara.Cli.Tests` project (mirroring the other `tests/Vara.*.Tests` projects) with a `Spectre.Console.Testing` reference, wired into the solution, and verify `dotnet test` discovers and runs an initial placeholder test
- [ ] 1.3 Publish the real `Vara.Cli` project AOT (`dotnet publish -r win-x64 --self-contained`) after adding the package reference and verify zero AOT/trim warnings and record the actual published binary size delta against today's baseline

## 2. Shared presentation conventions (`cli-presentation`)

- [ ] 2.1 Implement a small shared helper (or extension on `AnsiConsole`) exposing the three outcome-severity styles (clean success, partial failure, hard error) so every command applies them consistently, and verify with a `Vara.Cli.Tests` case per style using `TestConsole`
- [ ] 2.2 Verify (via `TestConsole` configured as redirected/non-interactive, and separately with `NO_COLOR` set) that styled output degrades to plain, uncolored text per the `cli-presentation` spec's fallback scenarios
- [ ] 2.3 Manually verify on a plain Windows `conhost` window (not just Windows Terminal) that colored/styled output renders correctly, per design.md's conhost VT risk

## 3. Backup progress display rework

- [ ] 3.1 Spike whether `AnsiConsole.Live`'s `AutoRefresh` redraws on its own cadence without an explicit `Refresh()` call or task-value change; confirm whether `ProgressHeartbeat`'s `Timer` can be removed outright or whether a minimal `ctx.Refresh()`-calling timer is still required, and record the outcome
- [ ] 3.2 Implement the two-row `Live` composite (row 1: `Spectre.Console.ProgressBar` sized to the console profile's width; row 2: a borderless fixed-column `Grid` rendering bytes transferred/total, throughput, and ETA from `BackupProgressCalculator`'s `ProgressSnapshot`), and verify with a `Vara.Cli.Tests`/`TestConsole` case that a value crossing a digit-count boundary does not shift the other fields' horizontal position
- [ ] 3.3 Wire the composite into `BackupCommand`, retaining `ProgressDisplayGate`'s monotonic "never regress" guard, and updating or removing `ProgressHeartbeat` per the 3.1 spike outcome
- [ ] 3.4 Verify the bar falls back to plain, non-progress-bar output (per `cli-presentation`) when the terminal width cannot be determined (for example, redirected output), with a `TestConsole` case
- [ ] 3.5 Implement color-coded run-summary reporting in `BackupCommand`/`BackupRunSummaryFormatter` (clean success vs. partial failure vs. hard error, per the `progress-reporting` delta), and verify with a `Vara.Cli.Tests` case for each of the three outcomes
- [ ] 3.6 Run the existing `Vara.Application.Tests` `Reporting` suite (`BackupProgressCalculatorTests`, `ProgressDisplayGateTests`, `ProgressHeartbeatTests`, `ProgressHeartbeatStallSimulationTests`, `BackupRunSummaryFormatterTests`) and update any test that assumed a now-removed `ProgressHeartbeat`/`ProgressDisplayGate` member, verifying the suite passes

## 4. Snapshot and history listings

- [ ] 4.1 Convert `SnapshotsCommand`'s output to a `Spectre.Console.Table` with headers, preserving the existing "no snapshots recorded" plain message for the empty case, and verify with a `Vara.Cli.Tests` case
- [ ] 4.2 Convert `HistoryCommand`'s output to a `Spectre.Console.Table` with headers, and verify with a `Vara.Cli.Tests` case

## 5. Prompts and error styling

- [ ] 5.1 Replace `RestoreCommand`'s `Console.ReadLine`-based overwrite prompt with `AnsiConsole.Confirm(...)`, keeping the existing `Console.IsInputRedirected` gate that decides whether to prompt at all unchanged, and verify the interactive/non-interactive/`--force` scenarios in `snapshot-history`'s existing restore requirements still behave identically (manual verification, since `RestoreCommand`'s profile/service setup isn't easily unit-testable without a real profile)
- [ ] 5.2 Update `ErrorReporting.Run` to render its error message in the hard-error style to standard error, and verify with a `Vara.Cli.Tests` case that the styled output still writes to stderr (not stdout) and preserves the existing `Error: <message>` text and exit code of 1
- [ ] 5.3 Update `PruneCommand`'s completion message to use the shared success styling from section 2, and verify with a `Vara.Cli.Tests` case

## 6. Final validation

- [ ] 6.1 Run the full test suite (`dotnet test`) across all projects and verify all tests pass
- [ ] 6.2 Manually run `vara backup <profile>` against a real profile with mixed file sizes and verify visually: bar on its own line, no jitter in the stats line, correct color on completion
- [ ] 6.3 Manually run `vara backup` with a failing file (for example, a file locked by another process) and verify the amber partial-failure styling appears
- [ ] 6.4 Manually run `vara backup > out.txt` (redirected) and verify plain, uncolored, non-progress-bar output with no stray escape sequences in `out.txt`
