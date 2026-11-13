## Why

`vara snapshot <id>` prints a static directory tree of a snapshot's changes. Large snapshots need repeated runs with different `--depth` values and directory scopes to find where the changes are. An interactive tree lets the user open and close directories in place, with every directory still showing its aggregated counts.

## What Changes

- Add `--interactive` to `vara snapshot <id> [directory]`. It implies the tree view and shows the same report as the static output (snapshot header, change tree, per-directory rollup counts, `[source]` marks), but the user navigates it with the keyboard.
- Navigation follows file-manager convention: Up/Down move the cursor; Right expands a collapsed directory or, when already expanded, moves to its first child; Left collapses an expanded directory or, on a leaf or collapsed directory, moves to its parent; Home/End jump to the first/last row; `*` expands all; `q` or Esc quits.
- Initial expansion follows `--depth <n>` (directories deeper than `n` start collapsed). Without `--depth`, the interactive view starts at depth 1. The static view keeps its unlimited default.
- Rollup counts are identical whether a directory is expanded or collapsed. A collapsed directory shows the counts of its whole subtree.
- With `--files`, changed files are leaf rows under their directory.
- The view scrolls within the terminal height and follows the cursor, and it redraws on terminal resize.
- When input or output is redirected, or color or live rendering is unavailable, `--interactive` falls back to the static tree and writes a one-line note to standard error.
- Introduce a neutral `ChangeEntry` input (mirror path, change kind, optional previous path) between the data source and the rollup, so a later change can feed the same tree from a backup plan. No behavior change for existing output.
- Out of scope: `--json`, the `backup` command (a separate follow-up change), and any change to the static tree's output.

## Capabilities

### New Capabilities

None.

### Modified Capabilities

- `snapshot-history`: new requirements for the `--interactive` option (navigation, initial expansion, counts), scrolling, and the fallback to the static report. The existing "Inspect changes recorded by a snapshot" requirement is unchanged.
- `cli-presentation`: new requirements for key-driven views (in-place repaint, terminal restore on exit, error and Ctrl+C) and for ASCII-safe markers in the interactive tree.

## Impact

- `src/Vara.Domain/Presentation/HistoryView.fs` and `src/Vara.Domain/Snapshots/History.fs`: `ChangeEntry` mapping and a pure tree-browser state and render function.
- `src/Vara.Cli/Commands/SnapshotCommand.fs`: new option, mode selection, fallback note.
- `src/Vara.Cli/Output`: a key-reading loop and in-place repaint, kept behind a record of functions like `Prompts` so tests can drive it with scripted keys.
- Tests: Domain tests for the state machine and rendering; CLI tests for the option, fallback and scripted key sessions.
- No new package dependency is expected. No manifest, engine or JSON contract changes.
