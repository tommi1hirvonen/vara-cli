## Why

The interactive change tree stops at the deepest directory that holds a change, and lists files only when `--files` was given up front. To see which files changed in a directory, the user has to quit and run again. Browsing is the point of the interactive view, so the files belong in it, with a symbol that tells what happened to each. Deleted files add noise in some snapshots and are the point of others, so the user needs to switch them on and off while browsing.

## What Changes

- The interactive tree always lists the changed files of a directory when it is expanded, including the deepest directories, with no need for `--files`. `--files` is still accepted with `--interactive` and has no further effect there. The static tree is unchanged, including its `--files` output.
- Each file row starts with a symbol for its change kind, in the change-kind colors: `+` added, `~` changed, `->` moved, `-` deleted, and `@` linked. A moved file's previous path and every file's full path are shown on a detail line for the row under the cursor, so rows stay short.
- The `d` key shows or hides deleted file rows. Deleted rows are shown by default.
- The `f` key shows or hides all file rows. File rows are shown by default.
- A deleted row that is the recorded source of a moved file in the same change set is not listed as a row of its own: the move reads as one line. Directory counts are unchanged, so a directory's deleted count can exceed its visible deleted rows by the number of moves out of it.
- The status line shows whether deleted rows and file rows are shown, and how many rows each toggle hides. Directory counts never change with either toggle. A directory with no visible children or file rows shows no expand marker.
- The browser builds an index (children of each directory, sorted file entries per directory) and keeps the visible rows cached, so moving the cursor does not rescan the tree. This is a prerequisite: with files, a large snapshot has hundreds of thousands of rows.
- `vara backup --interactive` gets all of this for dry runs and real runs, since it uses the same browser. It needs the recorded snapshot's file rows, which a real run reads when `--interactive` is given.
- Out of scope: symbols or file rows in the static tree, per-kind toggles other than `d`, search and filtering, and any change to counts, `--json`, or the plan.

## Capabilities

### New Capabilities

None.

### Modified Capabilities

- `snapshot-history`: the "Interactive snapshot change tree" requirement changes (files are shown without `--files`); new requirements cover the file rows and symbols, moved files absorbing their source's deleted row, and the `d` and `f` toggles with their status-line indicators.
- `cli-presentation`: a new requirement for file symbols that stay readable without Unicode or color.

The `backup-execution` requirement "Interactive backup tree and its fallback" already says the backup tree uses the snapshot tree's keys, expansion and scrolling, so it needs no delta.

## Impact

- `src/Vara.Domain/Presentation/TreeBrowser.fs`: index, cached rows, toggles, file rows, symbols, detail line, status line. New `Key` cases.
- `src/Vara.Cli/Output/KeyInput.fs`: maps `d` and `f`.
- `src/Vara.Cli/Commands/SnapshotCommand.fs`, `BackupCommand.fs` and `ChangeTree.fs`: interactive loads file entries; the browser takes the entries.
- `src/Vara.Domain/Snapshots/History.fs`: the browser gets the sorted entries beside the directory rollup. The static rollup is unchanged.
- Tests: Domain tests for the index, file rows, symbols, move pairing and toggles; CLI tests for the interactive default and both commands.
- Benchmarks: a browser scenario on 200,000 entries (build, Expand All, a stream of cursor steps and frames), measured before and after the index work, stored as a new baseline folder.
- README and command help text for the new keys.
