## 1. Outcome state in `BackupProgressColumn`

- [ ] 1.1 Add a `BackupProgressOutcome` enum (`Running`, `Success`, `PartialFailure`, `Error`) and a `BackupProgressColumn.OutcomeKey` constant, alongside the existing `RoleKey`/`ProgressKey`, and verify the project builds
- [ ] 1.2 In `RenderBar`, read the task's stamped `BackupProgressOutcome` from `State` (default to `Running` when unset) and set `_bar.CompletedStyle`/`_bar.FinishedStyle` per outcome before delegating to `_bar.Render`: both amber (`OutcomeStyle.PartialFailure`'s color) while `Running`; both the matching color (green/amber/red) for `Success`/`PartialFailure`/`Error`, forcing `task.Value = task.MaxValue` in the latter three cases so the bar renders fully filled in that color. Verify with a unit test asserting the rendered ANSI color for each of the four outcome states on a `Bar`-role task
- [ ] 1.3 Reference `OutcomeStyle`'s existing `Color` fields (`Success`, `PartialFailure`, `Error`, `Neutral`) from `BackupProgressColumn` instead of re-declaring the same `Color` constants, and verify no duplicate color literals remain for these four values

## 2. Scan phase: indeterminate bar instead of spinner

- [ ] 2.1 Remove the `_spinner` (`SpinnerColumn`) field from `BackupProgressColumn` and rewrite `RenderScan` to compose `_bar` (with `IsIndeterminate` driven by `task.IsIndeterminate`, unchanged from today) and a `" Scanning files..."` label via a two-column `Grid` (bar column sized to `availableWidth - labelText.Length`, label column fixed to `labelText.Length`) - the same technique `RenderBar` uses for bar+percentage. Verify with a unit test asserting the scan role still renders a single line containing `"Scanning files..."` and reaching the configured width
- [ ] 2.2 Set `_bar.IndeterminateStyle` to the pastel blue (`OutcomeStyle.Neutral`'s color) whenever the scan task's outcome is `Running` (the only state it's ever rendered in, per design.md), and the matching outcome color if a hard error is stamped onto the scan task before it's replaced. Verify with a unit test asserting the indeterminate pulse's ANSI color

## 3. `BackupCommand.RunWithLiveDisplay`: stamp the resolved outcome

- [ ] 3.1 Wrap the existing `result = pipeline.Run(profile, progress);` call in a `try`/`catch (Exception)`; on success, stamp `barTask` with `Success` if `result.FailedPaths.Count == 0` else `PartialFailure`, then call `ctx.Refresh()` once more before the callback returns. Verify with an integration-style test (or `TestConsole`-based test) that a run with no failed paths ends with the bar in the success color, and one with failed paths ends with the bar in the partial-failure color
- [ ] 3.2 In the `catch` block, stamp `Error` onto `barTask ?? scanTask` (whichever is non-null), call `ctx.Refresh()`, then `throw;` to preserve the original exception/stack trace unchanged. Verify with a test that induces a failure before `onTransferPhaseStarting` fires (only `scanTask` exists) and one after (both `barTask`/`scanTask` exist, `scanTask` already removed) each end with the active bar in the error color, and that the original exception still propagates to the caller with its type/message intact
- [ ] 3.3 Verify `ErrorReporting.Run`'s existing catch-all still reports the same message and exit code as before for both a recognized and an unrecognized exception raised from within `RunWithLiveDisplay` (no regression from the added `try`/`catch`)

## 4. Test updates for existing coverage

- [ ] 4.1 Update `BackupProgressColumnTests` assertions that currently expect a static `PaleGreen1` fill for an in-progress bar - they should now expect amber while `Running`, and verify `Bar_role_fills_completely_in_the_pastel_green_color_when_there_is_nothing_to_transfer` and `Bar_role_uses_the_pastel_green_finished_style_once_complete` are re-expressed against an explicitly stamped `Success` outcome rather than relying on `Value >= MaxValue` alone
- [ ] 4.2 Update `Scan_is_replaced_by_bar_and_stats_once_transfer_begins` (and any other scan-role test relying on `SpinnerColumn`/`"Scanning files..."` glyph output) to assert against the new indeterminate-bar rendering instead of a spinner glyph, and verify the test suite passes
- [ ] 4.3 Run the full `Vara.Cli.Tests` suite and verify all tests pass

## 5. Manual verification

- [ ] 5.1 Run a real backup against a profile with no failures in an interactive terminal (conhost and/or Windows Terminal) and verify: scan phase shows a full-width pastel-blue pulsing bar; transfer phase shows a full-width pastel-amber bar; on completion the bar turns entirely pastel green
- [ ] 5.2 Run a real backup against a profile with at least one unreadable/locked file and verify the bar turns entirely pastel amber (not green) on completion, consistent with the printed partial-failure summary below it
- [ ] 5.3 Force a hard error partway through a run (for example, an inaccessible target path) and verify the bar most recently displayed turns entirely pastel red before the error message is printed, and the process still exits with the same non-zero exit code as before this change
