## Why

The scan phase of a backup run displays a small braille spinner + text, which reads as visually "thinner"/less substantial than the full-width transfer bar that follows it - the likely source of the bar "feeling thin" compared to Spectre's native `Progress()` widget (confirmed by inspecting Spectre.Console 0.57.2 source: the transfer bar itself is already exactly one line tall with the same default vertical padding as any native `Progress()` bar, so no fix is needed there). Separately, the transfer bar's fill color is a static pastel green throughout the run and only happens to match "success" as a side effect of reaching 100% bytes transferred - it does not actually reflect the run's real outcome (a file can fail without the byte total ever reaching 100%, and a Move/Delete-only failure doesn't touch the byte total at all), so the bar can occasionally show green on a run that had failures, or never turn green on a run that fully succeeded but had a preceding lightweight-operation failure.

## What Changes

- Replace the scan phase's spinner + text indicator with Spectre's native indeterminate progress bar (the same full-width pulsing bar technique used by the transfer bar), recolored to the pastel blue already used for scan/neutral text, so the scan phase reads as the same weight of widget as the transfer bar that follows it.
- Change the transfer bar's in-progress fill color from pastel green to pastel amber, so an in-progress run is visually distinct from a completed one at a glance.
- Make the bar's final color outcome-driven rather than percentage-driven: explicitly set once the run's actual result is known (after the pipeline call returns, or is caught having thrown), instead of relying on the progress bar's own "reached 100%" switch, which does not reliably correspond to run outcome:
  - Full success (no failed paths): bar turns entirely pastel green.
  - Success with partial failures (one or more failed paths, run still completes): bar turns/stays pastel amber.
  - Hard error (the run throws before completing): bar turns pastel red, whichever synthetic task (scan or transfer) is on screen at the time of failure.
- No change to `BackupProgressCalculator`'s percent/throughput/ETA math, the fixed-width stats line layout, or `ProgressDisplayGate`'s rate-limiting/monotonic guard.

## Capabilities

### New Capabilities

(none)

### Modified Capabilities

- `progress-reporting`: the scan-phase indeterminate indicator changes from a spinner example to an indeterminate progress bar; a new requirement defines that the progress bar's color reflects the run's actual outcome severity (running/success/partial failure/hard error), reusing the pastel severity palette already defined by the `cli-presentation` capability.

## Impact

- `src/Vara.Cli/Presentation/BackupProgressColumn.cs`: scan role renders the shared `ProgressBarColumn` (indeterminate, pastel-blue pulse) instead of `SpinnerColumn`; bar role's fill styles become driven by an outcome state read from `ProgressTask.State`, not purely by `Value`/`MaxValue`.
- `src/Vara.Cli/Commands/BackupCommand.cs`: `RunWithLiveDisplay` gains explicit success/partial-failure/error outcome handling around the `pipeline.Run(...)` call, stamping the active synthetic task with the resolved outcome and forcing one final redraw before the live region closes (including on the exception path, before re-throwing to the existing `ErrorReporting.Run` catch-all).
- `tests/Vara.Cli.Tests/Presentation/BackupProgressColumnTests.cs`: existing color-focused assertions on `CompletedStyle`/`FinishedStyle` update to reflect amber-while-running and outcome-driven final coloring; new coverage for the indeterminate scan bar's pastel-blue pulse and for the red error-outcome case.
