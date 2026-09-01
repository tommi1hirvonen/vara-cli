## 1. Color palette

- [x] 1.1 Update `OutcomeStyle.Success`/`.PartialFailure`/`.Error` to use `Color.PaleGreen1`/`Color.LightGoldenrod2`/`Color.IndianRed` respectively (keeping the existing `Bold` decoration on all three), add a new neutral/in-progress `Style` using `Color.LightSkyBlue1`, and verify with a `Vara.Cli.Tests` case asserting each style's `Color` and `Decoration`

## 2. Native `Progress()` migration for backup progress

- [x] 2.1 Implement a single `BackupProgressColumn : ProgressColumn` that switches on the rendering task's `"role"` state (`"scan"`/`"bar"`/`"stats"`) and returns a different `IRenderable` per role - not one column per role - since verified empirically that a column's width is reserved on every task-row even when it renders blank, which would otherwise silently break the bar's full-width row; for `"stats"`, read a `BackupProgressCalculator`-produced `ProgressSnapshot` from a `BackupProgress` value stashed in the task's `State` (not the task's own `Value`/speed tracking), rendering bytes transferred/total, throughput, and ETA in fixed-width fields, and verify with a `Vara.Cli.Tests`/`TestConsole` case that a value crossing a digit-count boundary does not shift neighboring fields (carrying over the existing jitter-avoidance test intent from `BackupProgressPanelTests` or equivalent)
- [x] 2.2 Implement the `"bar"` role's rendering within `BackupProgressColumn`: delegate the fill segments to Spectre's own `ProgressBarColumn` (composition, not inheritance) with `CompletedStyle`/`FinishedStyle` both set to `Color.PaleGreen1` (matching `OutcomeStyle.Success` from task 1.1, replacing the old panel's `Color.Green`) and `RemainingStyle` to `Color.Grey`, combined with a hand-formatted percentage in a two-column `Grid` (not `Columns`, which was verified to stack a "greedy" `ProgressBarColumn` and a second renderable onto separate rows instead of one line) - both the bar's `Value`/`MaxValue` and the percentage text driven from `BackupProgressCalculator`'s percent (not raw bytes/total), so a zero-total run's bar fill and percentage text agree (`ProgressBarColumn`'s own fill ratio does not special-case a zero `MaxValue` the way `ProgressTask.Percentage` does) - and verify with `TestConsole` cases that the bar+percentage line reaches the full configured console width, uses `Color.PaleGreen1` for both the in-progress and 100%-finished states, and fills completely (not empty/grey) when there is nothing to transfer
- [x] 2.3 Wire the "scan" (spinner), "bar", and "stats" synthetic tasks into a single `AnsiConsole.Progress()` call in `BackupCommand` using the one `BackupProgressColumn`, and verify with a `TestConsole` case that the rendered output has three distinct rows in the expected order (scan replaced by bar+stats once transfer begins)
- [x] 2.4 Wire `ProgressDisplayGate`'s existing monotonic "never regress" guard and rate-limit to gate updates into the "bar"/"stats" tasks' `Value`/`State`, and verify the existing `ProgressDisplayGate` unit tests still pass unmodified
- [x] 2.5 Remove `BackupProgressPanel` and the manual heartbeat `Timer`/`HeartbeatInterval` from `BackupCommand`, relying on `Progress()`'s built-in `AutoRefresh`, and verify by manually running a backup with a stall (or a `TestConsole` case simulating elapsed time) that throughput/ETA continue to advance without any new progress event
- [x] 2.6 Verify the non-interactive/redirected fallback path (`RunWithPlainOutput` in `BackupCommand`) is unaffected by this migration - it does not use `Progress()` - and its existing `TestConsole` coverage still passes

## 3. Scan-phase spinner

- [x] 3.1 Add the `"scan"`-role indeterminate task to the `Progress()` run, started before `scanner.Scan(...)`/hashing begins and completed once the total set of changed files/bytes is known, and verify with a `TestConsole` case that a spinner/description renders during this phase
- [x] 3.2 Verify with a `TestConsole` case that the scan task is no longer rendered once the "bar"/"stats" tasks begin (per the `progress-reporting` delta's "Indicator replaced once transfer begins" scenario)

## 4. Backup summary coloring scope

- [x] 4.1 Update `BackupOutcomeReporter` to render only `BackupRunSummaryFormatter.Format`'s first line (the completion headline) in the severity `Style`, and the remaining lines in `Style.Plain`, and verify with a `Vara.Cli.Tests` case (per line count/style) for both the clean-success and partial-failure outcomes
- [x] 4.2 Verify the existing `BackupRunSummaryFormatter` unit tests (string content/formatting) still pass unmodified, since the formatter itself is not changed

## 5. Unclassified-exception catch-all

- [x] 5.1 Add a final `catch (Exception ex)` clause to `ErrorReporting.Run` that renders `Unhandled exception: {ex.GetType().Name}: {ex.Message}` in `OutcomeStyle.Error` followed by `ex.StackTrace` in a dim style, and returns exit code 1 - not `AnsiConsole.WriteException`, which was verified during implementation to be explicitly unsupported under Native AOT (`RequiresDynamicCodeAttribute`, reproduced as a real `IL3050` warning on `dotnet publish`) and this project publishes with `PublishAot` enabled - and verify with a `Vara.Cli.Tests` case using `TestConsole` that an unrecognized exception type produces styled output on the injected console and exit code 1
- [x] 5.2 Verify with a `Vara.Cli.Tests` case that a recognized exception (for example, `ProfileConfigException`) still takes the existing friendly-message path unchanged (same message text, no stack trace, same exit code), so the new catch-all does not shadow it

## 6. Snapshot and history table polish

- [x] 6.1 Add `.Border(TableBorder.Rounded)` to the `Table` constructed in `SnapshotsTablePresenter` and `HistoryTablePresenter`, and verify with a `TestConsole` case that the rendered border characters match the rounded style
- [x] 6.2 Render `SnapshotsTablePresenter`'s Status cell using `OutcomeStyle.Success`/`.Error`/the neutral in-progress style added in task 1.1, keyed off `SnapshotStatus` (`Complete`/`Failed`/`Running`), and verify with a `TestConsole` case for all three statuses
- [x] 6.3 Right-align the numeric columns (Added/Changed/Moved/Deleted/Transferred) in `SnapshotsTablePresenter`, and verify with a `TestConsole` case
- [x] 6.4 Add distinct styles per `FileChangeKind` local to `HistoryTablePresenter` - `Added` using `Color.PaleGreen1`, `Changed` using `Color.LightGoldenrod2`, `Moved` using `Color.LightSkyBlue1`, `Deleted` using `Color.IndianRed` - render the Change cell with the matching style, and verify with a `TestConsole` case covering all four kinds
- [x] 6.5 Right-align the Size column in `HistoryTablePresenter`, and verify with a `TestConsole` case
- [x] 6.6 Add a "No version history recorded for this path." (or similar) empty-state message to `HistoryTablePresenter`, mirroring `SnapshotsTablePresenter`'s existing empty case, and verify with a `TestConsole` case that an empty version list produces that message rather than a headers-only table

## 7. `RestoreCommand` consistency fixes

- [x] 7.1 Replace `RestoreCommand`'s `Console.Error.WriteLine("Error: specify either --at <date> or --version <id>.")` with the same styled error path `ErrorReporting`/`OutcomeStyle.Error` uses elsewhere, preserving the existing exit code of 1, and verify with a `Vara.Cli.Tests` case
- [x] 7.2 Replace `RestoreCommand`'s raw `Console.WriteLine` calls for "Restored '...' to '...'" with `OutcomeStyle.WriteLineSuccess`, and verify with a `Vara.Cli.Tests` case
- [x] 7.3 Replace `RestoreCommand`'s raw `Console.WriteLine("Restore cancelled: destination not overwritten.")` with the shared styled path (partial/neutral styling as appropriate), and verify with a `Vara.Cli.Tests` case
- [x] 7.4 Manually re-verify the interactive overwrite prompt, `--force`, and non-interactive scenarios behave identically to before (same gate logic, only the surrounding messages restyled), per the same manual-verification approach used in `improve-cli-output-presentation`'s task 5.1 (confirmed by user in a real terminal)

## 8. Final validation

- [x] 8.1 Run the full test suite (`dotnet test`) across all projects and verify all tests pass
- [x] 8.2 Manually run `vara backup <profile>` against a real profile with mixed file sizes and verify visually: scan spinner during the initial phase, bar+stats rows during transfer (pastel green bar fill), only the headline colored in the completion summary, rounded table borders elsewhere (ran against a real profile with mixed file sizes (5 small files + a 5 MB file); correct byte totals/formatting confirmed end-to-end - this sandboxed shell's stdout is always redirected, so it only exercised the plain-text fallback path; the interactive spinner/bar/color rendering needs **your** confirmation in a real terminal)
- [x] 8.3 Manually run `vara snapshots` and `vara history` against a real profile and verify visually: rounded borders, right-aligned numeric columns, pastel status/change-kind coloring (rounded borders and right-aligned numeric columns confirmed via the real CLI's output; color codes are covered by the `TestConsole`-based unit tests since this shell's output is uncolored)
- [x] 8.4 Manually trigger an unrecognized exception (for example, a transient I/O error) and verify it renders as a styled, readable exception on stderr rather than a raw stack trace, with exit code 1 (reproduced end-to-end with a malformed YAML profile config, which raises a raw `YamlDotNet.Core.SemanticErrorException` that `TryGetFriendlyMessage` does not recognize: rendered as "Unhandled exception: SemanticErrorException: ..." followed by the full stack trace, exit code 1)
- [x] 8.5 Manually run `vara backup > out.txt` (redirected) and verify plain, uncolored, non-progress output is unaffected by this change (no stray escape sequences, no attempted spinner/bar rendering) (verified: `out.txt` contains zero ESC (0x1B) characters, plain multi-line progress text as before)
