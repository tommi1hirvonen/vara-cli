## Context

See `proposal.md` for motivation. Today's backup progress is a custom `IRenderable` (`BackupProgressPanel`) hosted in `AnsiConsole.Live(...)`, driven by a manual heartbeat `Timer` because `LiveDisplay` has no built-in auto-refresh (confirmed by the prior `improve-cli-output-presentation` change's spike). That change rejected `AnsiConsole.Progress()` because a single `ProgressTask`'s columns render side-by-side in one row, which seemed to preclude stacking a bar row and a heterogeneous stats row - and because `Spectre.Console.ProgressBar` turned out to be an internal type.

Since then, inspecting `Spectre.Console` 0.57.2 (the version already referenced) confirms two things that change the calculus:
- `ProgressColumn` is a public, extensible base class. A custom column's `Render(RenderOptions, ProgressTask task, TimeSpan deltaTime)` can read arbitrary state via `task.State` instead of the task's own built-in increment/speed tracking - so a custom column can pull throughput/ETA straight from the existing `BackupProgressCalculator`, sidestepping `RemainingTimeColumn`'s internal speed math entirely.
- `Progress()` renders one row per *task*, not per column set restricted to a single task. Two synthetic tasks in the same `Progress()` run - one carrying only a full-width bar column, one carrying only the stats columns - render as two stacked rows, because each task is its own row. Custom columns decide what (if anything) to render for a given row by checking which synthetic task they're rendering.
- `Progress()` (unlike `Live`) has built-in `AutoRefresh`, so the manual heartbeat `Timer` is no longer needed at all - not even in the reduced "call `ctx.Refresh()` periodically" form the prior change ended up with.
- Conhost behavior is unaffected either way: `Live` and `Progress` both render through the same `AnsiConsoleFacade`/backend-selection plumbing (`AnsiConsoleBackend` when VT is supported, `LegacyConsoleBackend` otherwise), so switching from one to the other carries no new terminal-compatibility risk.

## Goals / Non-Goals

**Goals:**
- Replace `BackupProgressPanel`/`Live` with `AnsiConsole.Progress()` while preserving every existing `progress-reporting` requirement byte-for-byte (dedicated-width bar line, non-shifting fixed-width fields, monotonic guarantee, rate-limiting).
- Cover the previously-silent scan/hash phase with a native indeterminate spinner task.
- Scope severity coloring to the outcome indicator everywhere it's used, not just in the backup summary.
- Give every command a safety net for exceptions that aren't already mapped to a friendly message.
- Establish one rounded-corner border convention applied to every current and future bordered widget.

**Non-Goals:**
- No change to `BackupProgressCalculator`'s percent/throughput/ETA math, or to `ProgressDisplayGate`'s rate-limiting/monotonic guard - both are reused unchanged.
- No change to which commands prompt, which exceptions get a friendly message today, or any exit code - the catch-all only covers what previously had *no* handling at all.
- No introduction of a `Panel`-wrapped progress display - `Progress()` owns its own `Live` region and cannot be wrapped in an outer bordered widget; the rounded-corner convention therefore applies to tables now, and to any panel-style widget introduced later.

## Decisions

### Backup progress: two synthetic `Progress()` tasks, not one
Create the `Progress()` instance with an ordered column list that branches per-row on a role stashed in each task's `State` (`"role"` = `"scan"` | `"bar"` | `"stats"`):
- A `SpinnerColumn` + description column pair that only renders for the `"scan"` task (started first, marked complete once enumeration/hashing finishes and the transfer tasks are added).
- A description-suppressing column + a full-width custom `BarColumn` (or `ProgressBarColumn`, sized via `GridColumn`-style remaining-width allocation) that only renders for the `"bar"` task.
- A custom `EtaStatsColumn` (bytes transferred/total, throughput, ETA, all fixed-width) that only renders for the `"stats"` task, reading a `BackupProgressCalculator`-produced `ProgressSnapshot` stashed in that task's `State` on every `Report` callback - not the task's own `Value`/`MaxValue`-derived speed.
Columns for a task whose role doesn't match render an empty `Text("")`, so each task occupies exactly one row with only its own content, faking the two-(then three-)row stacked layout `Progress()` doesn't natively support for a single task.

Alternatives considered: a single task with all columns on one row - rejected, directly conflicts with the `progress-reporting` requirement that the bar gets a "dedicated, width-sized line" free of other content. Keeping the existing custom `Live` composite and only adding a `SpinnerColumn`-equivalent by hand - rejected, forgoes `AutoRefresh` and the point of this change.

### `ProgressDisplayGate` stays; the heartbeat `Timer` is deleted outright
`ProgressDisplayGate`'s monotonic "never regress" guard and 100 ms rate-limit are rendering-agnostic domain logic and carry over unchanged, gating calls into the `"bar"`/`"stats"` tasks' `Value`/`State` updates exactly as they gated `ctx.Refresh()` before. The heartbeat `Timer` (already reduced to a minimal `ctx.Refresh()` caller by the prior change) is deleted rather than reduced further: `Progress()`'s `AutoRefresh` redraws on its own cadence, and each column recomputes elapsed-time-based throughput/ETA fresh from `BackupProgressCalculator` on every redraw, exactly as `BackupProgressPanel.Render` did - so nothing needs to trigger a redraw manually during a stall.

### Scan-phase spinner: a leading indeterminate task
Add the `"scan"`-role task before calling `scanner.Scan(...)`/hashing, using `Value = 0`, `IsIndeterminate = true` (or `MaxValue = double.NaN` per Spectre's indeterminate-task convention) so `SpinnerColumn` animates without a percentage. Mark it complete (or remove it) once the scan/hash phase finishes and the `"bar"`/`"stats"` tasks are added, satisfying the new `progress-reporting` requirement's "indicator replaced once transfer begins" scenario.

### Summary coloring: split the formatted string at render time, not in `BackupRunSummaryFormatter`
`BackupRunSummaryFormatter.Format` keeps returning one multi-line string (unchanged - it's covered by its own existing tests and requirement). `BackupOutcomeReporter` changes to render the first line (the "Snapshot #N completed in..." headline) with the severity `Style`, and the remaining lines (added/changed/moved/deleted/transferred/failed) with `Style.Plain`, using two `console.Write` calls instead of one `WriteLine` with a single style. Alternatives considered: reworking `BackupRunSummaryFormatter` to return a structured result (headline + body) instead of a flat string - rejected as unnecessary churn to an already-tested, stable formatter; a simple index-of-first-newline split at the presentation boundary is sufficient and keeps the formatter presentation-agnostic.

### Unhandled exceptions: a final `catch` in `ErrorReporting.Run`, using `AnsiConsole.WriteException`
Add `catch (Exception ex)` after the existing `when (TryGetFriendlyMessage(...))` catch, calling `errorConsole.WriteException(ex, ExceptionFormats.ShortenPaths | ExceptionFormats.ShortenTypes)` (Spectre's recommended default combination for readable output) and returning exit code 1, mirroring the existing recognized-exception path's exit code. `WriteException` renders in its own built-in red/error styling, consistent with `OutcomeStyle.Error` in spirit without needing to force it through the same `Style` object (Spectre's exception renderer manages its own multi-color highlighting for types/members - overriding it would fight the widget). This is a pure catch-all: it only ever fires for exceptions `TryGetFriendlyMessage` doesn't already recognize, so no existing exit code or message for a known exception changes.

### Rounded corners: `Table.Border(TableBorder.Rounded)` applied at each table's construction site
`SnapshotsTablePresenter` and `HistoryTablePresenter` both call `.Border(TableBorder.Rounded)` when constructing their `Table`. No shared helper is introduced for this alone (two call sites, one line each) - if a third bordered widget appears later, revisit extracting a shared constant/helper then.

### Status/change-kind coloring: reuse `OutcomeStyle`'s existing `Style` values as table cell styles
`SnapshotsTablePresenter` renders the Status cell as `new Text(status.ToString(), OutcomeStyle.Success | .PartialFailure | .Error)` keyed off `SnapshotStatus` (`Complete` -> success, `Failed` -> error, `Running` -> a new neutral/in-progress style added to `OutcomeStyle` since neither existing style fits an in-progress row). `HistoryTablePresenter` colors `FileChangeKind` with new, distinct styles (not reusing the three severity styles as a strict 1:1 mapping, since "added/changed/moved/deleted" isn't a severity axis) - kept local to `HistoryTablePresenter` since no other command needs file-change-kind coloring today. See the "Color palette" decision below for the exact colors each of these styles uses.

### Color palette: pastel tones, not Spectre's default named colors
Every color introduced or changed by this proposal - the progress bar's fill, `OutcomeStyle.Success`/`.PartialFailure`/`.Error`, the new neutral/in-progress style, and `HistoryTablePresenter`'s `FileChangeKind` colors - uses a softer, pastel-toned color from Spectre's extended 256-color palette (https://spectreconsole.net/console/reference/color-reference/) instead of Spectre's default named `Green`/`Yellow`/`Red`, which read as sharp, saturated "neon" shades on most terminal color schemes:

| Role | Color | Hex |
| --- | --- | --- |
| Success (clean outcome; progress bar fill; `FileChangeKind.Added`) | `PaleGreen1` | `#87FFAF` |
| Partial failure / amber (`FileChangeKind.Changed`) | `LightGoldenrod2` | `#D7D787` |
| Hard error (`FileChangeKind.Deleted`) | `IndianRed` | `#AF5F5F` |
| Neutral / in-progress (`SnapshotStatus.Running`; `FileChangeKind.Moved`) | `LightSkyBlue1` | `#AFD7FF` |

`Bold` decoration is kept on the three severity styles exactly as before - pastel tone reduces saturation, not the weight that keeps a styled outcome legible and distinct from surrounding plain text. The progress bar's filled segment switches from `Color.Green` to the same `PaleGreen1` used for `OutcomeStyle.Success`, so a completed run's success color is visually consistent with the bar that led up to it. Alternatives considered: keeping the default named colors and only changing structure/scope (this proposal's original plan) - superseded once explicitly requested; using 24-bit RGB literals instead of Spectre's named extended-palette constants - rejected, since the named constants (`Color.PaleGreen1`, etc.) are self-documenting at the call site and map onto terminals that only support a 256-color palette without any conversion loss.

## Risks / Trade-offs

- [Risk] Faking two/three rows via synthetic tasks and per-role column branching is a Spectre usage pattern without first-party precedent in Spectre's own docs → Mitigation: cover it with a `Vara.Cli.Tests`/`TestConsole` case asserting the exact rendered row layout (bar row content, stats row content, no cross-talk between roles), same rigor the prior change applied to the `Live` composite.
- [Risk] `AnsiConsole.WriteException`'s default formatting could be overly verbose or expose internal type names to end users for a genuine unexpected bug → Mitigation: use `ExceptionFormats.ShortenPaths | ShortenTypes` to keep it as readable as practical; this is still strictly better than today's unhandled raw stack trace, and the existing friendly-message path is untouched for every already-known exception.
- [Trade-off] Deleting the heartbeat `Timer` entirely (rather than keeping a minimal version) means the redraw cadence is now whatever `Progress()`'s `AutoRefresh` uses internally, not the previously-tuned 500 ms `HeartbeatInterval` → acceptable, since `AutoRefresh`'s default cadence is already tuned by Spectre for smooth terminal redraws and `ProgressDisplayGate` still bounds how often the underlying values change.
- [Risk] The pastel palette's lighter, less-saturated tones could have lower contrast than the default named colors on a light-background terminal theme (this codebase has so far only manually verified colored output against dark-background conhost/Windows Terminal) → Mitigation: all three severity styles keep their existing `Bold` decoration, which preserves legibility independent of hue; revisit the exact shades during manual verification (tasks 7.2/7.3) if a light-theme terminal shows a legibility problem, since the specific hex values are cosmetic and not a requirement.
