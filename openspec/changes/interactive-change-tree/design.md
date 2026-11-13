## Context

`vara snapshot` builds its report in two pure steps: `History.rollup` turns a flat list of `FileVersion` rows into `RollupDirectory list` (subtree counts per directory, files per directory), and `HistoryView.ofRollup` turns that into a `Display`, applying `--depth` as a filter. The CLI then hands the `Display` to `LiveRenderer` or `PlainRenderer`, which map tree glyphs to ASCII on consoles without Unicode (`Display.toAscii`).

Interactive input today is limited to Spectre prompts, wrapped in the `Prompts` record so tests can script answers. Nothing reads raw keys. `Capabilities` already carries `InputIsInteractive`, `IsTerminal`, `SupportsColor` and `NoColorRequested`, and `OutputMode.choose` turns them into `Live` or `Plain`.

Rollup counts already include every descendant, so a collapsed directory needs no extra data. A later change will feed the same tree from a backup plan, so the tree must not depend on `FileVersion`. See proposal.md for scope.

## Goals / Non-Goals

**Goals:**
- A tree browser whose behavior can be tested without a terminal.
- A source-neutral input, so the backup plan can reuse the browser later.
- No change to existing static output.

**Non-Goals:**
- Mouse input, search, filtering, or acting on entries (restore, show).
- Any new dependency.
- Building the `backup` integration or the plan data it needs.

## Decisions

**1. State machine in Domain, key loop in Cli.** The browser is `TreeState` plus `step: Key -> TreeState -> TreeState` and `render: height -> TreeState -> Line list`, all in `Vara.Domain`. `Key` is a small union (Up, Down, Left, Right, Home, End, ExpandAll, Quit, Other). The Cli project maps `ConsoleKeyInfo` to `Key` and runs the loop. A `KeyInput` record of functions (read a key, current height and width, repaint) sits beside `Prompts` so tests drive a whole session with scripted keys. Alternative considered: a Spectre `Live` display with a custom key pump. Rejected because `Live` owns the console during updates, gives little control over scrolling and cursor restoration, and cannot be unit tested without a console.

**2. Neutral `ChangeEntry` between source and rollup.** `ChangeEntry` is `{ Path: string; Kind: ChangeKind; PreviousPath: string voption }`. `History.rollup` keeps its signature for existing callers, implemented over a new `rollupEntries` that takes `ChangeEntry`; a mapping function converts `FileVersion` to `ChangeEntry`. The tree needs the file text (`name - kind`, moved-from), which is derived from `ChangeEntry` alone, so no domain type from the manifest leaks into the browser. Alternative considered: make the browser generic over the row type. Rejected as more type machinery than one small record.

**3. Visible rows are derived, not stored.** `TreeState` holds the rollup, the set of expanded directory paths (case-insensitive, like the rest of the history code), the cursor as a row identity (a directory path or a directory path plus file index), and the scroll offset. The visible row list is computed from the rollup and the expanded set on each step. Keeping the cursor as an identity, not an index, makes collapse and expand-all keep the cursor on the same logical row, falling back to the nearest visible ancestor when its row disappears. Trees are at most tens of thousands of directories, so recomputing is cheap; if a benchmark shows otherwise, cache the list in the state.

**4. Initial expansion rule.** A directory starts expanded when its depth below the scope root is less than the effective depth, where the effective depth is the `--depth` value or 1 if omitted. So `--depth 0` starts fully collapsed (only the root row; Right opens it), and the default shows the root with its immediate children. The static output's "N deeper directories not shown" note does not apply in the interactive tree, because every directory is reachable.

**5. Rendering reuses `Display` and the existing renderers' mapping.** `render` returns `Line list` using the same spans and tones as `ofRollup` (counts in change-kind tones, `[source]` marks). Tree guides keep the existing glyphs, so `Display.toAscii` covers them. New markers are ASCII-safe by construction: `[+]` for collapsed, `[-]` for expanded, spaces for leaves, `>` as the cursor gutter, and the cursor row additionally in a reverse or bold style. Hidden-row indicators are plain text such as `(3 more above)`. Because nothing new is outside ASCII, the "no Unicode" requirement costs one test and no change to `toAscii`. Alternative considered: Unicode triangles with new `toAscii` entries. Rejected as more mapping for no gain.

**6. Terminal handling.** The loop switches to the alternate screen, hides the cursor, sets `Console.TreatControlCAsInput` so Ctrl+C arrives as a key (mapped to the cancellation outcome), and restores all of it in a `finally`, so an exception or Ctrl+C leaves the terminal as it was. The final screen is left clean; the report is not reprinted after quitting. Each repaint writes the whole frame from the home position, clearing to the end of each line, to avoid flicker from full clears. Height and width are read each iteration, and a changed size triggers a redraw. Because key reads block, a resize with no key press is picked up by polling `KeyAvailable` on a short interval. Alternative considered: Windows-only console APIs. Rejected; the polling approach also works on other platforms and terminals.

**7. Scrolling.** The window is the terminal height minus the header and a status line, with at least one row. The scroll offset is adjusted after every step so the cursor row is inside the window and, after a collapse, the window does not leave blank rows while rows are hidden above. The status line shows `(N more above)` and `(M more below)` and the key hints.

**8. Fallback decision in one place.** The `snapshot` handler decides before loading anything: interactive only when `--interactive` is given and input is not redirected, output is a terminal, color is supported and `NO_COLOR` is unset. Otherwise it prints the static report and writes one line to standard error. Empty and unknown-snapshot results are reported before this decision, so they never enter the loop.

**9. Source marks and `--files`.** The `[source]` mark logic from `ofRollup` is extracted so static and interactive rendering share it. With `--files`, file rows are leaves under their directory, sorted as in the static view.

## Risks / Trade-offs

- [Terminal behavior differs between Windows Terminal, legacy conhost and other hosts, especially alternate screen and Ctrl+C handling] → Start with a spike task on the loop and repaint; keep the terminal calls behind `KeyInput` so the fallback to the static report remains available if a host misbehaves.
- [Resize polling adds idle wakeups] → Poll at a coarse interval (around 100 ms) and only while waiting for a key.
- [Repaint flicker on large frames] → Write each frame as a single buffered string without clearing the whole screen.
- [Key sessions are hard to test end to end] → Cover the state machine and rendering with Domain tests, the loop with scripted `KeyInput`, and one manual check per terminal host recorded in the tasks.
- [Moving `rollup` onto `ChangeEntry` could alter existing output] → Keep existing rollup and `ofRollup` tests unchanged and passing as the guard.

## Open Questions

- Whether Enter should toggle a directory as an alias for Right/Left is deferred; it does not affect the specs.
