## Why

`restore` only ever operates on one file at a time. Recovering an entire directory (or a whole
profile) as of a given date - arguably the most common real recovery scenario, e.g. "undo
yesterday's mistake across this folder" - currently means running `restore` once per file, after
first working out which files existed at that date via `history`/`browse`. This change lets
`restore` target a directory and reconstruct its exact point-in-time state in one operation.

## What Changes

- `restore`'s path argument may name a directory instead of a single file, guarded by a new
  explicit `--recursive` flag (mutually exclusive with `--version`, which has no meaning for a
  whole subtree) - so a directory is only ever restored when the user unambiguously asks for one.
- A recursive restore reconstructs the *exact* state of every tracked path under that directory
  as of `--at` (or the current state if `--at` is omitted): files that existed then are written,
  including ones since deleted from the mirror; a file present at the destination that did **not**
  exist under that directory at that date **is removed**, so the destination ends up matching the
  historical tree exactly rather than a merge of old and current content. **BREAKING** in the
  sense that it is a new, more destructive capability than any existing `restore` behavior
  (deletes files at the destination), though strictly additive to the CLI surface.
- Exactly one confirmation prompt covers the whole operation: before writing anything, the system
  reports how many files will be written and how many (if any) will be removed from the
  destination, and asks for a single explicit confirmation - replacing the per-file overwrite
  prompt for this mode. `--force` skips the prompt the same way it already does for a single-file
  restore.
- The same mirror-containment guard, `--out`/`--in-place` destination modes, and flexible
  path-resolution rules that already govern single-file restore apply per file during a
  recursive restore.
- Progress reporting extends to cover a whole recursive restore: since every file's size is known
  upfront (the same manifest data that already powers `browse`'s point-in-time listing), the
  system displays a single byte-based progress bar spanning all files, sized against the total
  bytes to be written across the whole operation, with no separate scan phase.

## Capabilities

### Modified Capabilities
- `snapshot-history`: adds a directory/subtree restore mode to the existing restore
  requirements, including its own single-confirmation and point-in-time-reconstruction
  (delete-what-shouldn't-be-there) behavior.
- `progress-reporting`: the existing byte-based, upfront-total-known progress requirements
  extend to a recursive restore's multiple files, aggregated into one bar instead of one
  per file.

## Impact

- `Vara.Cli/Commands/RestoreCommand.cs`: new `--recursive` flag, directory-path handling,
  single confirmation prompt, and a multi-file progress bar.
- `Vara.Application/History/SnapshotHistoryService.cs`: new method to resolve and restore every
  tracked path under a directory prefix as of a given date (or now), reusing
  `ISnapshotRepository.GetStateAsOf`/`GetCurrentState`/`GetTombstones` (already added for
  `browse`) rather than introducing new repository queries.
- `Vara.Core/Abstractions/IContentStore.cs` and its implementation: no new members expected -
  `ExtractTo` and mirror-containment/existence checks are reused per file; a directory-restore
  helper may be added if deleting extraneous destination files needs a store-level primitive.
- Test files for each of the above.
