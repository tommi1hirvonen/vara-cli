## 1. Color palette

- [ ] 1.1 Update `OutcomeStyle.Success`/`.PartialFailure`/`.Error` to use `Color.PaleGreen1`/`Color.LightGoldenrod2`/`Color.IndianRed` respectively (keeping the existing `Bold` decoration on all three), add a new neutral/in-progress `Style` using `Color.LightSkyBlue1`, and verify with a `Vara.Cli.Tests` case asserting each style's `Color` and `Decoration`

## 2. Native `Progress()` migration for backup progress

- [ ] 2.1 Implement a custom `EtaStatsColumn : ProgressColumn` that reads a `BackupProgressCalculator`-produced `ProgressSnapshot` from the relevant task's `State` (not the task's own `Value`/speed tracking) and renders bytes transferred/total, throughput, and ETA in fixed-width fields, and verify with a `Vara.Cli.Tests`/`TestConsole` case that a value crossing a digit-count boundary does not shift neighboring fields (carrying over the existing jitter-avoidance test intent from `BackupProgressPanelTests` or equivalent)
- [ ] 2.2 Implement a custom full-width bar column (or configure `ProgressBarColumn` to consume all remaining width) that renders only for the `"bar"`-role task using `Color.PaleGreen1` for the filled segment (matching `OutcomeStyle.Success` from task 1.1, replacing the old panel's `Color.Green`) and rendering nothing for other roles, and verify with a `TestConsole` case that the bar reaches (near) the full configured console width and uses the expected color
- [ ] 2.3 Wire the "scan" (spinner), "bar", and "stats" synthetic tasks into a single `AnsiConsole.Progress()` call in `BackupCommand`, each task's columns branching on a `"role"` value stashed in `task.State`, and verify with a `TestConsole` case that the rendered output has three distinct rows in the expected order (scan replaced by bar+stats once transfer begins)
- [ ] 2.4 Wire `ProgressDisplayGate`'s existing monotonic "never regress" guard and rate-limit to gate updates into the "bar"/"stats" tasks' `Value`/`State`, and verify the existing `ProgressDisplayGate` unit tests still pass unmodified
- [ ] 2.5 Remove `BackupProgressPanel` and the manual heartbeat `Timer`/`HeartbeatInterval` from `BackupCommand`, relying on `Progress()`'s built-in `AutoRefresh`, and verify by manually running a backup with a stall (or a `TestConsole` case simulating elapsed time) that throughput/ETA continue to advance without any new progress event
- [ ] 2.6 Verify the non-interactive/redirected fallback path (`RunWithPlainOutput` in `BackupCommand`) is unaffected by this migration - it does not use `Progress()` - and its existing `TestConsole` coverage still passes

## 3. Scan-phase spinner

- [ ] 3.1 Add the `"scan"`-role indeterminate task to the `Progress()` run, started before `scanner.Scan(...)`/hashing begins and completed once the total set of changed files/bytes is known, and verify with a `TestConsole` case that a spinner/description renders during this phase
- [ ] 3.2 Verify with a `TestConsole` case that the scan task is no longer rendered once the "bar"/"stats" tasks begin (per the `progress-reporting` delta's "Indicator replaced once transfer begins" scenario)

## 4. Backup summary coloring scope

- [ ] 4.1 Update `BackupOutcomeReporter` to render only `BackupRunSummaryFormatter.Format`'s first line (the completion headline) in the severity `Style`, and the remaining lines in `Style.Plain`, and verify with a `Vara.Cli.Tests` case (per line count/style) for both the clean-success and partial-failure outcomes
- [ ] 4.2 Verify the existing `BackupRunSummaryFormatter` unit tests (string content/formatting) still pass unmodified, since the formatter itself is not changed

## 5. Unclassified-exception catch-all

- [ ] 5.1 Add a final `catch (Exception ex)` clause to `ErrorReporting.Run` that calls `errorConsole.WriteException(ex, ExceptionFormats.ShortenPaths | ExceptionFormats.ShortenTypes)` and returns exit code 1, and verify with a `Vara.Cli.Tests` case using `TestConsole` that an unrecognized exception type produces styled output on the injected console and exit code 1
- [ ] 5.2 Verify with a `Vara.Cli.Tests` case that a recognized exception (for example, `ProfileConfigException`) still takes the existing friendly-message path unchanged (same message text, no stack trace, same exit code), so the new catch-all does not shadow it

## 6. Snapshot and history table polish

- [ ] 6.1 Add `.Border(TableBorder.Rounded)` to the `Table` constructed in `SnapshotsTablePresenter` and `HistoryTablePresenter`, and verify with a `TestConsole` case that the rendered border characters match the rounded style
- [ ] 6.2 Render `SnapshotsTablePresenter`'s Status cell using `OutcomeStyle.Success`/`.Error`/the neutral in-progress style added in task 1.1, keyed off `SnapshotStatus` (`Complete`/`Failed`/`Running`), and verify with a `TestConsole` case for all three statuses
- [ ] 6.3 Right-align the numeric columns (Added/Changed/Moved/Deleted/Transferred) in `SnapshotsTablePresenter`, and verify with a `TestConsole` case
- [ ] 6.4 Add distinct styles per `FileChangeKind` local to `HistoryTablePresenter` - `Added` using `Color.PaleGreen1`, `Changed` using `Color.LightGoldenrod2`, `Moved` using `Color.LightSkyBlue1`, `Deleted` using `Color.IndianRed` - render the Change cell with the matching style, and verify with a `TestConsole` case covering all four kinds
- [ ] 6.5 Right-align the Size column in `HistoryTablePresenter`, and verify with a `TestConsole` case
- [ ] 6.6 Add a "No version history recorded for this path." (or similar) empty-state message to `HistoryTablePresenter`, mirroring `SnapshotsTablePresenter`'s existing empty case, and verify with a `TestConsole` case that an empty version list produces that message rather than a headers-only table

## 7. `RestoreCommand` consistency fixes

- [ ] 7.1 Replace `RestoreCommand`'s `Console.Error.WriteLine("Error: specify either --at <date> or --version <id>.")` with the same styled error path `ErrorReporting`/`OutcomeStyle.Error` uses elsewhere, preserving the existing exit code of 1, and verify with a `Vara.Cli.Tests` case
- [ ] 7.2 Replace `RestoreCommand`'s raw `Console.WriteLine` calls for "Restored '...' to '...'" with `OutcomeStyle.WriteLineSuccess`, and verify with a `Vara.Cli.Tests` case
- [ ] 7.3 Replace `RestoreCommand`'s raw `Console.WriteLine("Restore cancelled: destination not overwritten.")` with the shared styled path (partial/neutral styling as appropriate), and verify with a `Vara.Cli.Tests` case
- [ ] 7.4 Manually re-verify the interactive overwrite prompt, `--force`, and non-interactive scenarios behave identically to before (same gate logic, only the surrounding messages restyled), per the same manual-verification approach used in `improve-cli-output-presentation`'s task 5.1

## 8. Final validation

- [ ] 8.1 Run the full test suite (`dotnet test`) across all projects and verify all tests pass
- [ ] 8.2 Manually run `vara backup <profile>` against a real profile with mixed file sizes and verify visually: scan spinner during the initial phase, bar+stats rows during transfer (pastel green bar fill), only the headline colored in the completion summary, rounded table borders elsewhere
- [ ] 8.3 Manually run `vara snapshots` and `vara history` against a real profile and verify visually: rounded borders, right-aligned numeric columns, pastel status/change-kind coloring
- [ ] 8.4 Manually trigger an unrecognized exception (for example, a transient I/O error) and verify it renders as a styled, readable exception on stderr rather than a raw stack trace, with exit code 1
- [ ] 8.5 Manually run `vara backup > out.txt` (redirected) and verify plain, uncolored, non-progress output is unaffected by this change (no stray escape sequences, no attempted spinner/bar rendering)
