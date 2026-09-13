## Context

The `vara profiles` command provides an interactive terminal user interface for creating, editing, and deleting backup profiles. Currently, the command runs entirely within the terminal's primary screen buffer.

During manual verification in a previous change, `IAnsiConsole.Clear()` was tried and rejected because calling `Clear()` in the primary screen buffer cleared the entire terminal window, erasing previous shell commands and scrollback history that had nothing to do with `vara`. To avoid that, a localized `ClearOwnRegion` helper was introduced to capture the initial cursor row (`CursorTop + 1`) and blank lines downwards using space-padding.

However, when managing sources or performing multiple edits:
1. Sub-screens (`RunSourcesScreen`, `EditSource`, `EditStringList`) perform no clearing at all.
2. The sequence of prompts and renders outputs dozens of lines, exceeding the terminal viewport height and forcing the terminal to scroll vertically.
3. In ANSI terminal emulators, cursor positioning (`CSI row;col H`) is relative to the active visible viewport, not the scroll buffer. Once lines scroll into the terminal's scrollback history, ANSI cursor sequences cannot reach or erase them.
4. `ClearOwnRegion` desynchronizes, leaving stale menus at the top of the viewport and duplicate copies throughout the scrollback history.

See `proposal.md` for motivation and `specs/profile-management/spec.md` for requirements.

## Goals / Non-Goals

**Goals:**
- Run `vara profiles` in the terminal's Alternate Screen Buffer (`\x1b[?1049h` / `\x1b[?1049l`), providing an isolated canvas for the TUI session.
- Eliminate all leftover output, ghost menus, and scrollback pollution during profile and source management.
- Preserve 100% of the user's prior terminal command history and scrollback when exiting `vara profiles`.
- Replace fragile cursor-position tracking (`ClearOwnRegion`) with standard `console.Clear()` across all screens.
- Ensure terminal restoration is guaranteed on normal exit (Quit), Save, Discard, errors, and cancellation (Ctrl+C).

**Non-Goals:**
- Converting non-interactive or stream-oriented commands (`backup`, `restore`, `diff`, `show`) to alternate screen buffers.
- Changing the underlying profile data structures, validation rules, or YAML persistence formats.
- Rewriting the UI using full TUI frameworks; Spectre.Console's built-in prompts and `AlternateScreen` support are sufficient.

## Decisions

### Decision: Use Spectre.Console's `console.AlternateScreen(Action)` for session lifecycle

Wrap the interactive session in `ProfilesCommand.Create` with `console.AlternateScreen(() => RunMenu(...))`.

**Rationale:**
- Spectre.Console provides first-class support for switching to the alternate screen buffer and switching back in a `try/finally` block.
- In the alternate screen buffer, the terminal does not maintain a scrollback buffer; the TUI operates on a bounded screen canvas.
- Upon leaving the alternate screen buffer (`\x1b[?1049l`), the terminal emulator automatically restores the primary screen buffer and cursor position to the exact state it was in before `vara profiles` started.

**Alternatives considered:**
- *Scoped cursor erasing in every sub-screen*: Rejected because once output scrolls past the bottom of the viewport, lines pushed into the terminal's scrollback buffer cannot be erased by ANSI cursor controls.
- *Manual ANSI escape sequences*: Rejected because Spectre.Console already provides an integrated, tested extension method (`AnsiConsoleExtensions.AlternateScreen`).

### Decision: Standard `console.Clear()` on every screen and sub-screen redraw

Replace `ClearOwnRegion` and the `screenStartRow` cursor calculations with standard `console.Clear()` at the top of every screen loop:
- `RunMenu`: Clear before rendering the main menu prompt.
- `RunEditScreen`: Clear before rendering `ProfileDraftPresenter.Render` and the edit menu.
- `RunSourcesScreen`: Clear before rendering the sources list and menu.
- `EditSource`: Clear before rendering `ProfileDraftPresenter.RenderSource` and the source field actions menu.
- `EditStringList`: Clear before rendering the string list and action prompt.

**Rationale:**
Inside the alternate screen buffer, `console.Clear()` is completely safe and instantaneous. It wipes only the temporary TUI canvas, without touching prior shell output. This eliminates all off-by-one errors and cursor math.

### Decision: Cooperative cancellation to guarantee terminal restoration

Currently, `ProfilesCommand` registers cancellation via:
```csharp
using var cancellationRegistration = RegisterCancellationExit(cancellationToken, Environment.Exit);
```
`Environment.Exit` terminates the process immediately, bypassing `finally` blocks. If a user presses Ctrl+C while in the alternate screen buffer, this could leave some terminal emulators stuck in alternate screen mode.

Instead, cancellation will signal a cooperative exit or invoke a cleanup handler that ensures the alternate buffer exit sequence (`\x1b[?1049l`) and cursor visibility are flushed before process exit.

**Rationale:**
TUI applications must always leave the user's terminal in a healthy, usable state, even when interrupted by SIGINT / Ctrl+C.

### Decision: Clear outcome messaging upon completion

When exiting back to the main menu or saving a profile:
- Within the TUI: Display success and error notifications at the top of the relevant screen after clearing (e.g. "Saved profile '...'" banner above the main menu).
- Upon exiting to the shell: If a profile was saved during the session, write a brief confirmation to standard output on the primary buffer so the user has immediate feedback in their terminal history.

## Risks / Trade-offs

- [Process killed abruptly via SIGKILL or debugger] → Any TUI (including vim and htop) can leave the alternate buffer if forcibly killed with SIGKILL (which cannot be caught). Handled via standard cancellation handling for normal signals (Ctrl+C).
- [Terminal does not support Alternate Screen Buffer] → `ProfilesCommand` already asserts `OutputMode.IsLiveCapable(console)` and `!Console.IsInputRedirected`. Modern terminals (Windows Terminal, VS Code terminal, ConEmu, Linux/macOS terminals) fully support alternate buffer modes. If `AlternateBuffer` capability is false, `AlternateScreen` throws an `InvalidOperationException` which is caught and surfaced via `ErrorReporting`.
