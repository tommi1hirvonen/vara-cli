## Context

Two independent TUI rendering issues, both diagnosed during exploration (see proposal.md for the
"why"):

1. `RestoreCommand.RunWithLiveDisplay` and `RestoreCommand.RunDirectoryRestoreWithLiveDisplay`
   (src/Vara.Cli/Commands/RestoreCommand.cs) each configure `console.Progress()` with Spectre's
   stock columns (`TaskDescriptionColumn`, `ProgressBarColumn`, `PercentageColumn`,
   `RemainingTimeColumn`, `TransferSpeedColumn`) and build the task's description from the
   restored path, e.g. `$"Restoring '{Markup.Escape(path)}'"`. `TaskDescriptionColumn` already
   truncates with an ellipsis at render time, but Spectre's row-layout measures every column's
   *unclipped* content first to decide how much width each column gets when the row doesn't fit -
   so a long path's raw length steals width from the bar during that measurement pass, before the
   description column's own truncation ever applies. `ProgressBarColumn.Width` defaults to a fixed
   `40`, but that fixed value is itself subject to the same proportional shrink once the row's
   total measured width exceeds the terminal width.
2. `ProfilesCommand.RunEditScreen` (src/Vara.Cli/Commands/ProfilesCommand.cs) runs a `while(true)`
   loop that calls `ProfileDraftPresenter.Render(console, draft)` (pure `WriteLine` calls, no
   in-place redraw) on every iteration, and returns straight to the main menu prompt on
   Save/Discard. Nothing in the command ever calls `console.Clear()`, so every re-render - within
   one edit session and across sessions - appends below whatever is already on screen.

`profiles` already requires an interactive, live-capable terminal before it runs at all (checked
in `ProfilesCommand.Create`), so relying on `IAnsiConsole.Clear()` being supported is safe without
any new capability check.

## Goals / Non-Goals

**Goals:**
- Guarantee the restore live-progress bar never renders below a usable width, regardless of the
  restored path's length, for both the single-file and recursive restore live-display paths.
- Guarantee the `profiles` edit screen never leaves a previously rendered draft/menu on screen
  underneath a new render.
- Keep both fixes small and localized: no change to the non-interactive/plain-output fallback
  paths, no change to other commands' progress displays (backup/prune/check are unaffected - see
  Alternatives below).

**Non-Goals:**
- Replacing Spectre's stock progress columns with a fully custom column (as `BackupProgressColumn`
  does) for restore. Restore's live display has no scan phase and no need for backup's
  three-role/outcome-coloring machinery; reusing that heavier pattern here would be more change
  than the problem needs.
- Redesigning `profiles` around a `Live` region instead of sequential prompts. The screen is
  prompt-driven, not continuously redrawn; a plain, deliberate `Clear()` before each render is
  sufficient and much less invasive.

## Decisions

### Decision: Pre-truncate the label string instead of a custom column
Rather than introduce a new `ProgressColumn` subclass for restore, truncate the path to a fixed
character budget *before* constructing the task's description string, so Spectre's stock
`TaskDescriptionColumn` never measures more than a bounded-length string. This keeps
`TaskDescriptionColumn`/`ProgressBarColumn`/`PercentageColumn`/`RemainingTimeColumn`/
`TransferSpeedColumn` unchanged and reuses the same "fixed budget" spirit `BackupProgressColumn`
already established, without needing a new column class or a per-frame render hook (restore's
description is set once at `AddTask` time, not updated every frame the way backup's bar/stats
rows are).

**Alternatives considered:**
- A custom column mirroring `BackupProgressColumn`'s fixed-width `Grid` layout - rejected as
  disproportionate for a label-only problem; would also require restore to drop
  `RemainingTimeColumn`/`TransferSpeedColumn` in favor of hand-rolled equivalents, since a custom
  column collapses cleanly with other custom columns but not by itself with stock ones sharing the
  same row's width budget.
- Letting `TaskDescriptionColumn.Wrap = true` wrap the label onto extra lines instead of truncating
  it - rejected because it does not solve the underlying issue (Spectre still measures the
  unwrapped content's width for row layout purposes) and would make the progress display taller
  and less predictable.

### Decision: Truncation algorithm - drop leading parent segments, prefix with `...`
Implement a small, shared helper (e.g. `Presentation/PathLabelTruncator`) that:
1. Returns the path unchanged if its length is within the budget.
2. Otherwise, splits the path into segments and greedily keeps as many *trailing* segments
   (ending in the filename) as fit within `budget - "...".Length`, prefixing the kept segments
   with `...`.
3. If even the filename alone exceeds the budget, truncates the filename itself (e.g. keeping its
   start and an ellipsis) so the label always fits.

This matches the parent-truncation shape raised during exploration (`.../parent/dir/file.txt`)
and keeps the most useful part of the path - the filename - visible longest as the path gets
progressively longer.

**Alternatives considered:**
- Basename-only display (`Restoring 'file.txt'`) - rejected: ambiguous for a recursive restore
  touching same-named files in different directories, and discards context for no benefit once a
  bounded-width solution exists anyway.
- Displaying the path on its own line above the bar - rejected: breaks the one-line-per-task
  convention every other progress display (backup, prune, check) already follows.

### Decision: Budget derivation
Compute the label's character budget once per live-display invocation from the real terminal
width (`console.Profile.Width`) minus the other columns' known/typical rendered widths (bar
`Width` of 40, plus headroom for `PercentageColumn`, `RemainingTimeColumn`, and
`TransferSpeedColumn`'s typical content and inter-column padding), clamped to a sane minimum floor
so an unusually narrow terminal still gets a short-but-non-empty label rather than a negative or
zero budget.

### Decision: `console.Clear()` placement in `RunEditScreen`
Call `console.Clear()` immediately before `ProfileDraftPresenter.Render(console, draft)` inside the
`while(true)` loop, and once more immediately before returning to the main menu on both the
`Save` and `Discard` branches. This covers every path that currently leaves stale content behind:
re-entering the loop after editing a field, and leaving the edit screen entirely.

**Alternatives considered:**
- Clearing only right after Discard - rejected: leaves the compounding-within-one-session case
  (editing multiple fields in a row) unfixed, since each loop iteration's `Render` call would still
  stack beneath the previous one.

## Risks / Trade-offs

- [Terminal width reported by `console.Profile.Width` is stale or wrong in some hosting
  environments] → Clamp the computed label budget to a sane minimum floor; a slightly
  over-generous or over-conservative budget still degrades gracefully (either a longer label than
  ideal, or a bit more truncation than strictly necessary) rather than breaking layout.
- [`console.Clear()` produces a visible flicker on some terminals] → `profiles` already requires a
  live-capable, real interactive terminal (checked before the command runs at all), so this is the
  same class of terminal the existing progress bars already redraw against; no new compatibility
  risk is introduced.
- [Filename-truncation fallback loses the ability to distinguish two same-directory files whose
  names differ only after the truncation point] → Accepted: this is an extreme-edge case (a
  single filename alone exceeding the label budget), and the alternative (an unbounded label) is
  the exact problem being fixed.
