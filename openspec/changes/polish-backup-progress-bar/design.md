## Context

See `proposal.md` for motivation. Today's implementation, per the prior `improve-cli-output-presentation` change's design.md:

- `BackupProgressColumn` (a single `ProgressColumn` shared across all rendered rows) branches on a `BackupProgressRole` (`Scan`/`Bar`/`Stats`) stashed in each synthetic `ProgressTask.State`.
- `RenderScan` composes an internal `SpinnerColumn` instance with a literal `" Scanning files..."` text via `Columns(...)`.
- `RenderBar` composes an internal `ProgressBarColumn` instance (`_bar`) with a hand-formatted percentage via a two-column `Grid`, driving `_bar`'s `Value`/`MaxValue` from `BackupProgressCalculator`'s 0-100 percent. `_bar.CompletedStyle` and `_bar.FinishedStyle` are both `PaleGreen1` today - `ProgressBarColumn` itself already switches from `CompletedStyle` to `FinishedStyle` the instant `Value >= MaxValue`, which is what makes today's bar happen to look "done" once bytes reach the total.
- `BackupCommand.RunWithLiveDisplay` owns the `Progress()` run: it creates `scanTask` up front, and lazily replaces it with `barTask`/`statsTask` on the first admitted progress report (which fires unconditionally once transfer begins, even for a zero-byte run, per `onTransferPhaseStarting`). `result = pipeline.Run(profile, progress)` is the last statement in the `ctx.Start(...)` callback; nothing currently runs after it before the callback returns and the live region closes.

Two things confirmed directly against the installed Spectre.Console 0.57.2 source (not assumed) inform this design:
- `ProgressBar.Render` (`Spectre.Console/Widgets/ProgressBar.cs`) always yields exactly one line; `DefaultProgressRenderer.Update` wraps the whole task grid in a single `Padder(..., new Padding(0, 1))` (unless `ExcludeVerticalPadding` is set, which nothing here sets). Both are identical regardless of which columns are configured, so the transfer bar is not structurally thinner than a native `Progress()` bar - no change needed there.
- `ProgressBar`'s indeterminate "pulse" rendering (used when `IsIndeterminate = true`) is already a public, usable code path through the existing `ProgressBarColumn.Render` (it forwards `task.IsIndeterminate` and an `IndeterminateStyle`) - no custom animation code is needed to switch the scan phase from a spinner to an indeterminate bar.

## Goals / Non-Goals

**Goals:**
- Scan phase renders via the same shared `ProgressBarColumn` instance as the transfer phase (indeterminate), recolored to the pastel blue already used for scan/neutral text (`Color.LightSkyBlue1`), instead of Spectre's default pulse colors.
- Transfer bar's in-progress fill is pastel amber (`Color.LightGoldenrod2`, same constant as `OutcomeStyle.PartialFailure`).
- The bar's final color is driven explicitly from the run's actual outcome (success / partial failure / hard error), not from `ProgressBarColumn`'s own `Value >= MaxValue` switch, so it is correct even when a failure doesn't move the byte total to 100% (or never touches it at all, e.g. a Move/Delete-only failure).
- A hard error (an exception escaping `pipeline.Run`) recolors whichever synthetic task is currently on screen (scan or transfer) to pastel red before the exception continues propagating to the existing `ErrorReporting.Run` catch-all - no change to that catch-all's behavior, message, or exit code.

**Non-Goals:**
- No change to `BackupProgressCalculator`'s percent/throughput/ETA math, `ProgressDisplayGate`'s rate-limiting/monotonic guard, or the fixed-width stats grid layout.
- No change to `BackupRunSummaryFormatter` or `BackupOutcomeReporter`'s post-run printed summary - this change is scoped to the live in-progress bar only.
- No change to `cli-presentation`'s `OutcomeStyle` values themselves - this change reuses the existing pastel constants, it does not introduce new ones.
- No attempt to make the bar's automatic 100%-triggers-`FinishedStyle` behavior "just work" for outcome coloring - superseded by explicit outcome-driven coloring per the Decisions below.

## Decisions

### One outcome enum stashed in task `State`, read by both scan and bar rendering
Add a `BackupProgressOutcome` enum (`Running`, `Success`, `PartialFailure`, `Error`) and a new `BackupProgressColumn.OutcomeKey`, stashed via `ProgressTask.State.Update` exactly like the existing `RoleKey`/`ProgressKey`. Both `RenderScan` and `RenderBar` read it before rendering and set the shared `_bar` instance's `IndeterminateStyle`/`CompletedStyle`/`FinishedStyle` for that render call:
- `Running`: `IndeterminateStyle` = pastel blue (scan), `CompletedStyle` = `FinishedStyle` = pastel amber (transfer) - i.e. the transfer bar never auto-flips to green from reaching 100%, because both fill styles are the same amber while `Running`.
- `Success` / `PartialFailure` / `Error`: `CompletedStyle` = `FinishedStyle` = the matching pastel color (green/amber/red), and the task's `Value` is forced to `MaxValue` so the whole bar renders filled in that color rather than partially filled at whatever percentage the run happened to reach.

Mutating the shared `_bar` instance's style properties per render call is the same established pattern `RenderBar` already uses for `_bar.Width` - safe here because only one bar-role task is ever rendering at a given moment (scan and transfer never coexist; the scan task is removed before the transfer tasks are added).

Alternatives considered: giving `BackupProgressColumn` its own `Style`-selection method keyed only on `task.Value`/`task.IsFinished` (no new state) - rejected, this is exactly the percentage-driven behavior the proposal identifies as wrong (doesn't fire on a partial failure that never reaches 100%, and can't distinguish success from partial failure or hard error at all, since both would sit at "finished").

### `BackupCommand.RunWithLiveDisplay` resolves and stamps the outcome itself, once, after `pipeline.Run` returns or throws
Wrap the existing `result = pipeline.Run(profile, progress);` call in a `try`/`catch`:
- On success, resolve `Running` -> `Success` if `result.FailedPaths.Count == 0`, else `PartialFailure`; stamp it onto `barTask` (guaranteed non-null by this point, since `onTransferPhaseStarting` always fires before `pipeline.Run` returns normally - even for a zero-byte run) and call `ctx.Refresh()` once more before the callback returns.
- On any exception, stamp `Error` onto whichever of `barTask`/`scanTask` is non-null (an exception can occur before transfer begins, e.g. during scanning/diffing/planning, while only `scanTask` exists), call `ctx.Refresh()`, then re-throw unchanged - `ErrorReporting.Run`'s existing catch-all continues to see the same exception, message, and exit code as before.

Alternatives considered: resolving the outcome inside `BackupProgressColumn` itself (e.g. by giving it a reference to the eventual `BackupRunResult`) - rejected, `BackupProgressColumn` is a pure rendering column with no knowledge of the pipeline or its result today, and reaching into it from render time would invert the existing data flow (state is pushed into tasks from the command, not pulled by the column) for no benefit over stamping `State` the same way every other per-render value already is.

### Scan phase: reuse `_bar` via a `Grid`, not `Columns`, dropping `SpinnerColumn` entirely
`RenderScan` switches from composing `_spinner` (an internal `SpinnerColumn` instance) via `Columns(...)` to composing `_bar` via a two-column `Grid` (bar column sized to `availableWidth - labelText.Length`, label column fixed to `labelText.Length`) - the same technique `RenderBar` already uses for bar+percentage, and for the same reason documented in the prior change's design.md: `ProgressBarColumn`'s renderable is "greedy" (wants full width), and `Columns(...)` pushes a second child onto its own row instead of placing it alongside on the same line, whereas `Grid`'s explicit fixed column widths keep them on one line. `_spinner`/`SpinnerColumn` is removed from `BackupProgressColumn` entirely - nothing else uses it.

Alternatives considered: keeping `SpinnerColumn` for the glyph and only adding color - rejected, doesn't address the "thin" visual weight the proposal is fixing (a spinner glyph is a couple of characters wide regardless of color; only switching to a full-width bar changes that).

### Color values: reuse existing `Color` constants, no new palette entries
- Scan/neutral: `Color.LightSkyBlue1` (already `OutcomeStyle.Neutral` and the scan text's existing color).
- Running/partial failure: `Color.LightGoldenrod2` (already `OutcomeStyle.PartialFailure`).
- Success: `Color.PaleGreen1` (already `OutcomeStyle.Success`, and the bar's current color).
- Error: `Color.IndianRed` (already `OutcomeStyle.Error`).

No new constants are introduced; `BackupProgressColumn` could reference `OutcomeStyle`'s fields directly rather than re-declaring the same `Color` values, avoiding drift between the two if the palette is ever revisited.

## Risks / Trade-offs

- [Risk] Forcing `barTask.Value = barTask.MaxValue` on the final stamped frame means the bar always visually reads as "100% filled" once outcome-colored, even for a partial failure that stopped well short of the byte total → Mitigation: this is intentional - the requirement is that the *outcome* determines the bar's final appearance, not the byte total; the numeric percentage remains available in the stats line (unaffected by this change) for anyone who wants the literal figure.
- [Trade-off] Mutating the shared `_bar` instance's style properties per render call (rather than constructing a fresh `ProgressBarColumn` per role/outcome) continues the existing pattern from the prior change rather than introducing immutable per-state style objects → acceptable, consistent with how `_bar.Width` is already handled, and avoids allocating a new column instance on every redraw.
- [Risk] The hard-error path now performs one extra `ctx.Refresh()` inside a `catch` before re-throwing → Mitigation: `ctx.Refresh()` only redraws the already-rendered `Live` region and cannot itself throw in a way that would swallow the original exception; the `throw;` (not `throw ex;`) preserves the original stack trace exactly as `ErrorReporting.Run` expects today.
