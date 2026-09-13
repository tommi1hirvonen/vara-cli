## Why

The interactive `vara profiles` TUI currently renders in the terminal's primary screen buffer using ad-hoc cursor-based region clearing (`ClearOwnRegion`). When managing sources or performing sequential edits within a profile, the volume of output quickly exceeds the terminal viewport height, triggering vertical scrolling. Once lines scroll off into the terminal's scrollback buffer, ANSI cursor coordinates desynchronize and lines in the scrollback buffer cannot be erased, leaving duplicate menus, stale source details, and leftover output across the console.

Switching `vara profiles` to run in the terminal's Alternate Screen Buffer resolves this permanently: the entire TUI runs in an isolated screen canvas with full screen clearing on redraws, while the user's prior shell commands and scrollback history remain completely preserved and untouched when exiting.

## What Changes

- Wrap the interactive session of `vara profiles` inside Spectre.Console's `AlternateScreen` buffer lifecycle (`\x1b[?1049h` / `\x1b[?1049l`).
- Replace fragile cursor-position tracking (`System.Console.CursorTop`, `ClearOwnRegion`) with standard `console.Clear()` calls at the top of render loops across the interactive screens (`RunMenu`, `RunEditScreen`, `RunSourcesScreen`, `EditSource`, `EditStringList`).
- Ensure graceful cancellation (Ctrl+C) and error exits properly restore the primary terminal screen buffer so the user's terminal is never left in an inconsistent state.
- Write completion outcomes (such as `Saved profile '<name>'.`) to the standard console output on the primary buffer after leaving the alternate screen.

## Capabilities

### New Capabilities

(none)

### Modified Capabilities

- `profile-management`: The `vara profiles` command SHALL execute within the terminal's alternate screen buffer, clearing the screen on menu and sub-screen transitions without polluting the terminal's primary buffer or scrollback history, and restoring the primary screen buffer upon exit.

## Impact

- `src/Vara.Cli/Commands/ProfilesCommand.cs`: Wrap interactive execution in `console.AlternateScreen(...)`, replace `ClearOwnRegion` with `console.Clear()` across main and sub-screen loops, ensure exit back to main buffer prints save notifications cleanly.
- Terminal behavior: `vara profiles` acts as a clean full-screen TUI session while active, leaving zero leftover output in the shell when finished.
