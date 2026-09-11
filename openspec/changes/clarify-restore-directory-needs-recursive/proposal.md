## Why

Running `restore <path>` (without `--recursive`) on a path that is actually a tracked
*directory* currently fails with "No history exists for path '<path>'." - the same message
used for a path that was genuinely never backed up. This is misleading: the directory's history
does exist, the user simply forgot `--recursive`. The user hit this directly (`vara restore
subdir --in-place --profile default`) and, reasonably, mistook it for a resolution regression
similar to the one fixed by `fix-directory-root-source-resolution` - it is not; it is a distinct,
previously-uncovered gap in single-file `restore`'s error reporting.

## What Changes

- WHEN `restore` (without `--recursive`) is given a path that does not match any tracked *file*
  but does match a tracked *directory* (i.e. some tracked path, live or deleted, falls under it),
  the system SHALL report a distinct, clear error stating that the path is a directory and that
  `--recursive` is required, instead of the generic "no history exists" message.
- The existing "no history exists for path" message continues to apply, unchanged, to a path that
  matches neither a tracked file nor a tracked directory.
- No change to `restore --recursive`'s own directory resolution or error reporting, and no change
  to `history`/`show`/`diff`'s error reporting (they operate on files only, same as today).

## Capabilities

### New Capabilities
(none)

### Modified Capabilities
- `snapshot-history`: the "Unknown path or version reported clearly" requirement gains a new
  scenario distinguishing "path matches a tracked directory" from "path never tracked at all" for
  single-file `restore`.

## Impact

- `src/Vara.Cli/Commands/RestoreCommand.cs`: single-file restore branch needs a "is this
  unresolved path actually a tracked directory" check (reusing the existing
  `IsDirectoryTracked`/`ListDirectory` helper already used by `--recursive`) before falling
  through to today's file-history lookup and `NoHistoryForPathException`.
- `src/Vara.Core/Snapshots/SnapshotExceptions.cs`: a new exception type for "restore target is a
  tracked directory, not a file" distinct from `NoHistoryForPathException`.
- `tests/Vara.Cli.Tests/Commands/RestoreCommandTests.cs`: new coverage for restoring a tracked
  directory path without `--recursive`.
