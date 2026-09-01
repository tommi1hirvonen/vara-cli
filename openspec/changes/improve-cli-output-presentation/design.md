## Context

See `proposal.md` for motivation. Today's rendering lives entirely in `Vara.Cli.Commands.BackupCommand.Render` (a single `\r`-overwritten string) plus hand-padded `$"..."` interpolations in `SnapshotsCommand`/`HistoryCommand`, with no color, no terminal-capability detection, and no automated test coverage anywhere in `Vara.Cli`. `Vara.Application.Reporting` already isolates the *decision* of when to render (`ProgressDisplayGate`, `ProgressHeartbeat`) and the progress *math* (`BackupProgressCalculator`) from the *how* - this design only needs to replace the "how" and reconsider whether the "when" pieces are still needed once a rendering library owns its own refresh loop.

Empirically verified before this design was written (see proposal.md's Impact): `Spectre.Console` 0.57.2 publishes AOT/trim-clean with zero warnings, adding roughly 2 MB to a Native AOT `win-x64` binary.

## Goals / Non-Goals

**Goals:**
- Replace hand-rolled rendering with `Spectre.Console` as the single presentation library used by every `Vara.Cli` command.
- A progress display architecture that can grow (a future live throughput sparkline row) without re-architecting the render path again.
- Preserve every existing decision-logic behavior that isn't presentation (when the restore overwrite prompt appears, exit codes, rate-limiting/monotonic guarantees) exactly as-is.

**Non-Goals:**
- The future time-axis throughput graph itself (explicitly deferred to a later change).
- Any change to `BackupProgressCalculator`'s percent/throughput/ETA math, or to the backup pipeline's plan/execute stages.
- A full-screen TUI (alternate screen buffer, raw keyboard input, own event loop) - `Spectre.Console`'s `Live`/`Progress` APIs render in the normal scrollback, which is what the `cli-presentation` and `progress-reporting` deltas assume.

## Decisions

### Rendering foundation: `Spectre.Console`, not hand-rolled ANSI
Alternatives considered: hand-rolled ANSI escape sequences (rejected - we'd reimplement capability detection, width detection, and fixed-width column padding that `Spectre.Console` already provides and has already been verified AOT-clean); `Terminal.Gui`/a full TUI framework (rejected - takes over the whole screen and input loop, which is a larger behavioral and dependency commitment than this proposal's scope, and conflicts with the CLI's line-oriented, scrollback-friendly output model that other commands and scripts rely on).

### Progress display: a custom `Live` composite, not the `Progress()` task API
`Spectre.Console.AnsiConsole.Progress()` is the more obvious fit for a progress bar, but it models each unit of work as a "task" rendered as one row of fixed columns - convenient for a single bar+stats row, but awkward for stacking a heterogeneous second row (and, later, a third sparkline row) that isn't just another column on the same task. Instead, use `AnsiConsole.Live(renderable)` hosting a small composite (`Rows` of: row 1 = a `Spectre.Console.ProgressBar` sized to the live console profile's width; row 2 = a borderless, fixed-column `Grid` rendering `BackupProgressCalculator`'s `ProgressSnapshot` fields). Reusing `Spectre.Console.ProgressBar` (the same bar renderable `Progress()` uses internally) keeps the visual result consistent with the wider Spectre ecosystem while giving us row-level control. The row-2 `Grid`'s fixed column widths are what satisfies the "numeric fields do not shift" requirement - each field renders into its own fixed-width cell, not into a single interpolated string.

### `ProgressDisplayGate` and `ProgressHeartbeat`: keep the monotonic guard, drop the custom timer
`ProgressDisplayGate`'s monotonic "never regress" guard is domain logic independent of rendering and stays. Its 100 ms rate-limit and `ProgressHeartbeat`'s `Timer`-driven redraw exist today only because `Console.Write` had no refresh loop of its own; `Live`'s `AutoRefresh` (shared with `Progress()`) redraws on its own cadence regardless of whether new data arrived, which is exactly `ProgressHeartbeat`'s purpose. Plan: keep the monotonic guard (still needed - concurrent workers can still deliver `BackupProgress` out of order), but let `Live`'s auto-refresh redraw the composite each tick by having the row-2 `Grid` compute elapsed-time-based throughput/ETA fresh on every render rather than replaying a remembered snapshot. Verify this early (a small spike task) before deleting `ProgressHeartbeat`'s `Timer`; if `Live`'s auto-refresh turns out not to redraw without a `Value`/`Refresh()` call, keep a much simpler timer that just calls `ctx.Refresh()` (no need to remember and replay the last progress value, since the composite already reads current elapsed time itself).

### Snapshot/history listings: `Spectre.Console.Table`
Replace `SnapshotsCommand`/`HistoryCommand`'s manual `$"#{id,-6} ..."` interpolation with `Table` (headers, auto-sized columns). Directly satisfies the `snapshot-history` delta's "aligned table" requirement and doubles as the presentation building block for the richer, more interactive listing you mentioned wanting later (that follow-on work would layer `SelectionPrompt`/pagination on top of the same `Table`, not replace it).

### Restore confirmation prompt: keep today's interactivity gate, swap only the prompt widget
`RestoreCommand` already decides *whether* to prompt via an explicit `!Console.IsInputRedirected` check before entering its `catch` block. Keep that gate exactly as-is rather than trusting `AnsiConsole`'s own interactivity detection for this decision, so behavior cannot silently drift from the `snapshot-history` spec's existing interactive/non-interactive scenarios. Once the gate has already decided to prompt, use `AnsiConsole.Confirm(...)` purely to render and read it.

### Error styling: style at the `ErrorReporting` boundary only
`ErrorReporting.Run`'s single `Console.Error.WriteLine($"Error: {message}")` becomes a styled write via Spectre's console instance for standard error, applying the `cli-presentation` capability's error style. Centralizing it there (rather than in each command) means every command's hard-error path gets consistent styling for free, matching that capability's "regardless of which command produced the error" scenario.

### Testing: add a `Vara.Cli.Tests` project using `Spectre.Console.Testing`
There is no `Vara.Cli.Tests` project today. `Spectre.Console.Testing` provides a `TestConsole` (an `IAnsiConsole` you can inject and assert exact rendered output against, including color/markup), which is a better fit for verifying the jitter-fix and severity-color requirements than trying to capture real `Console` output. Introduce the project as part of this change rather than after it, given how much new presentation logic this change adds.

## Risks / Trade-offs

- [Risk] Touching every command's output with no prior `Vara.Cli` test coverage increases regression surface → Mitigation: add `Vara.Cli.Tests` with `Spectre.Console.Testing` alongside the presentation changes, prioritizing the new capability-detection, severity-color, and jitter-avoidance logic.
- [Risk] Legacy Windows `conhost` (as opposed to Windows Terminal) has historically needed VT processing explicitly enabled for ANSI rendering → Mitigation: `Spectre.Console` handles this itself as part of its capability probing; verify manually on plain `conhost` during implementation, not only Windows Terminal.
- [Risk] `Live`'s auto-refresh may not redraw purely on elapsed time without an explicit trigger, undermining the `ProgressHeartbeat` replacement → Mitigation: described above (fall back to a minimal timer calling `ctx.Refresh()`).
- [Trade-off] A custom `Live` composite is more upfront code than `Progress()`'s task API, but keeps the two-row (soon three-row) layout easy to extend without a later rewrite when the throughput sparkline is added.
- [Trade-off] `vara snapshots`/`vara history` output is no longer stable for line-oriented script parsing (called out as **BREAKING** in the proposal) - accepted, since no stable machine-readable output format was ever specified for these commands.

## Open Questions

- Exact color values (e.g. which amber/yellow shade) and bar fill characters are cosmetic and can be tuned visually during implementation without affecting any requirement above.
