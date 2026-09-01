## Why

Backup progress today is a single, uncolored `\r`-overwritten line that jitters as numeric values change length, and a completed run's success/failure outcome is visually indistinguishable from routine text. Other commands (`snapshots`, `history`) hand-format columns with manual padding. None of this scales well with terminal width, doesn't degrade gracefully when output is redirected or colors are unsupported, and gives the user no immediate visual cue for a run's outcome. Adopting Spectre.Console (verified empirically: fully AOT/trim-safe, no publish warnings, ~2 MB added to the published native binary) lets us fix all of this with a small, well-tested dependency instead of hand-rolling ANSI escape handling, terminal-capability detection, and column alignment ourselves - and gives us a consistent foundation (tables, styled prompts) for richer, more interactive listing commands later.

## What Changes

- Add `Spectre.Console` as a dependency of `Vara.Cli`, used as the shared rendering foundation for everything the CLI prints.
- Rework backup progress display into two lines: a progress bar spanning (near) the full terminal width on its own line, and a stats line below it (bytes transferred/total, throughput, ETA) with fixed-width fields so values no longer shift surrounding text as they change length.
- Color-code a completed backup run's outcome: bold green for a clean success, amber/yellow for a success with one or more failed files, red for a hard error.
- **BREAKING** (for scripted consumers): `vara snapshots` and `vara history` now render their listings as an aligned table instead of today's hand-padded plain-text lines; the displayed data is unchanged, but its exact text layout is not stable for line-oriented parsing.
- Terminal capability detection (redirected/non-interactive output, `NO_COLOR`, narrow or undetectable width) automatically falls back to plain, uncolored, non-progress-bar output, via Spectre's built-in profile detection rather than hand-rolled checks.
- Error messages (`ErrorReporting`) and the interactive restore-overwrite confirmation prompt adopt the same styling conventions, with no change to their existing decision logic (when a prompt is shown, when errors are reported, exit codes).

## Capabilities

### New Capabilities
- `cli-presentation`: cross-cutting terminal presentation conventions shared by every command - color coding for success/warning/error outcomes, and graceful degradation to plain output when the terminal is non-interactive, color-incapable, or of undetectable width.

### Modified Capabilities
- `progress-reporting`: the live backup progress display's layout (bar on its own full-width line, stats on a separate line) and the completed run's color-coded outcome reporting.
- `snapshot-history`: the `snapshots` and `history` commands' listing output is presented as an aligned table rather than hand-formatted text lines.

## Impact

- `Vara.Cli` project: new `Spectre.Console` package reference; rendering code in `BackupCommand`, `SnapshotsCommand`, `HistoryCommand`, `PruneCommand`, `RestoreCommand`, and `ErrorReporting`.
- `Vara.Application.Reporting`: `BackupProgressCalculator`'s percent/throughput/ETA math is unaffected; `ProgressDisplayGate`'s rate-limiting and `ProgressHeartbeat`'s timer-driven redraw are revisited in design, since Spectre's own live-refresh loop may absorb part of that responsibility.
- Published native binary size increases by roughly 2 MB (measured against an AOT-published minimal Spectre.Console sample; the real increase will be confirmed once integrated).
- No changes to persisted data, manifest schema, or command arguments - presentation only.
- No existing automated test coverage for `Vara.Cli`'s `Commands/` today, so this change carries no test-breakage risk from touched rendering code, but new tests should still be considered where practical (e.g. capability-detection fallback logic).
