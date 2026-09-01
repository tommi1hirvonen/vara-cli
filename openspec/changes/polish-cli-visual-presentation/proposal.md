## Why

The `improve-cli-output-presentation` change adopted `Spectre.Console` but used a hand-rolled `Live` composite for backup progress instead of the native `Progress()` widget, because `Progress()`'s one-task-one-row column model seemed unable to stack a bar row and a heterogeneous stats row. Investigation since then shows a custom `ProgressColumn` can read its own state via `ProgressTask.State` (bypassing the task's built-in speed tracking entirely), and that two synthetic tasks - not one - can fake the two-row layout natively, gaining `Progress()`'s built-in `AutoRefresh` (removing the manual heartbeat `Timer`) and native `SpinnerColumn` support for free. Separately, several presentation gaps and inconsistencies have accumulated: the backup run summary colors its entire multi-line detail block green/amber instead of just the outcome, listing tables don't apply the severity-color language the `cli-presentation` capability already defines elsewhere, an unclassified exception still crashes with a raw unstyled stack trace, and a few `RestoreCommand` messages bypass Spectre entirely. This change closes those gaps in one pass and establishes a single rounded-corner border convention as a shared visual identity across every command's tabular and bordered output.

## What Changes

- Replace the custom `Live(BackupProgressPanel)` composite with `AnsiConsole.Progress()`, using two synthetic per-run tasks (a full-width bar row, a stats row below it) driven by custom columns that read throughput/ETA/bytes from `BackupProgressCalculator` directly rather than from the task's own built-in speed tracking, preserving every existing progress-reporting requirement (dedicated-width bar line, non-shifting fixed-width fields, monotonic "never regress" guarantee via the existing `ProgressDisplayGate`).
- Remove the manual heartbeat `Timer` in `BackupCommand`, relying on `Progress()`'s built-in `AutoRefresh` to keep throughput/ETA advancing during a stall instead.
- Add an indeterminate spinner task ("Scanning files...") covering the scan/hash phase that runs before file transfer begins and today shows no feedback at all.
- Narrow the backup run summary's severity coloring to the outcome headline only; render the supporting detail lines (Added/Changed/Moved/Deleted/Transferred/Failed) in default style, so a completed run is no longer a wall of green or amber text.
- Add a catch-all in `ErrorReporting.Run` that renders any exception not already recognized as a known, friendly-message domain exception via `AnsiConsole.WriteException`, in the same hard-error style as a recognized error, instead of letting it propagate out and crash with a raw, unstyled .NET stack trace.
- Color-code the `snapshots` table's Status column (`Running`/`Complete`/`Failed`) and the `history` table's Change column (`Added`/`Changed`/`Moved`/`Deleted`), reusing the existing severity-style language instead of leaving these columns as uncolored plain text.
- Add a "no version history recorded" message to `HistoryTablePresenter`'s empty case, mirroring `SnapshotsTablePresenter`'s existing empty-state behavior instead of rendering a headers-only table.
- Right-align the numeric columns in both the `snapshots` and `history` tables.
- Route `RestoreCommand`'s remaining raw `Console.WriteLine`/`Console.Error.WriteLine` calls (the `--at`/`--version` validation error, and the restored/cancelled confirmation messages) through the same styled paths (`OutcomeStyle`, `StandardError`) every other command already uses.
- Adopt a single rounded-corner border convention (`TableBorder.Rounded` for tables; rounded `Panel` borders wherever a bordered widget is introduced) applied consistently across every command's tabular and bordered output, replacing today's inconsistent/default borders.
- Adopt a pastel color palette (for example, `PaleGreen1`/`LightGoldenrod2`/`IndianRed` rather than Spectre's default named `Green`/`Yellow`/`Red`) for every severity-styled and kind-styled output - the progress bar's fill, the outcome-severity styles, and the snapshot/history tables' status and change-kind colors - replacing the sharper, more "neon" default shades with a softer, more consistent tone.

## Capabilities

### New Capabilities
(none)

### Modified Capabilities
- `progress-reporting`: the backup progress display's rendering mechanism changes from a custom `Live` composite to native `Progress()` with synthetic bar/stats tasks and a scan-phase spinner; the run summary's outcome-severity coloring is scoped to the headline rather than the entire detail block.
- `cli-presentation`: severity-color styling is scoped to the outcome indicator rather than supporting detail text; unclassified/unhandled exceptions are also rendered in the hard-error style (with a formatted stack trace) rather than crashing unstyled; bordered widgets (tables, panels) consistently use rounded corners as the shared visual identity.
- `snapshot-history`: the `snapshots` and `history` commands' table output gains severity/kind color-coding on their status/change columns, right-aligned numeric columns, and a friendly empty-state message for `history` matching `snapshots`' existing behavior.

## Impact

- `Vara.Cli` project: `BackupCommand` (progress-display rewiring), `BackupProgressPanel` (removed, replaced by `Progress()` columns), `OutcomeStyle`/`BackupOutcomeReporter` (narrower coloring scope), `Composition/ErrorReporting` (catch-all exception rendering), `SnapshotsTablePresenter`/`HistoryTablePresenter` (coloring, alignment, empty-state, rounded borders), `RestoreCommand` (remaining raw `Console` calls replaced).
- `Vara.Cli.Tests`: existing `TestConsole`-based tests covering the removed `Live` composite, current summary coloring, and current table rendering need updating for the new rendering paths; new tests added for the spinner phase, scoped coloring, table coloring, and the `ErrorReporting` catch-all.
- No changes to `Vara.Application`/`Vara.Core` domain logic: `BackupProgressCalculator`'s math, `ProgressDisplayGate`'s rate-limiting/monotonic guarantees, and all backup/restore/prune decision logic are unaffected - this change is presentation-only.
- No changes to persisted data, manifest schema, or command arguments. No new breaking change to `snapshots`/`history` output beyond what `improve-cli-output-presentation` already called out (table layout was already declared unstable for line-oriented parsing); this change alters only color, border style, and column alignment, not column content or ordering.
