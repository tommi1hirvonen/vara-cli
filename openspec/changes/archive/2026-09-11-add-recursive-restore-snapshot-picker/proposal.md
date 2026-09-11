## Why

Restoring a single file interactively already offers a version picker when neither `--at`
nor `--version` is given. Restoring a directory with `--recursive` has no equivalent: with
no `--at`, it silently restores the directory's current tracked state, and there is no way
to browse or pick a prior point in time without already knowing (or guessing) a date/time
to pass to `--at`. Since every file changed during one backup run shares the same recorded
timestamp, the snapshots that touched a given directory subtree can be reliably enumerated
and offered as a selectable list, giving directory restore the same interactive discoverability
single-file restore already has.

## What Changes

- **BREAKING**: When `vara restore --recursive <dir>` is run in an interactive session with
  neither `--at` nor a new snapshot-selection option given, the system now presents a
  selectable list of snapshots that touched the directory (plus a "current tracked state"
  entry, pre-selected so pressing Enter reproduces today's default) instead of silently
  restoring the current state. Non-interactive (redirected) sessions keep today's behavior
  unchanged: omitting `--at` still silently restores the current state.
- Add a new `--snapshot <id>` option to `restore --recursive`, mutually exclusive with
  `--at`, letting a specific snapshot be selected non-interactively (mirroring `--version`'s
  role for single-file restore).
- Each entry in the picker shows, scoped to the directory being restored: the change counts
  introduced by that snapshot (added/changed/moved/deleted/linked and net bytes), and the
  directory's total file count and byte size as of that snapshot - symlinks/junctions counted
  and reported as their own separate figure, not folded into the regular file count.
- A snapshot's `Cancelled` status is visually distinguished from `Complete` in the picker,
  reusing the same coloring already used by the `snapshots` command.
- Add a repository query that returns every recorded file-version change under a given
  directory prefix, across all snapshots, to support enumerating candidate snapshots and
  computing their directory-scoped change statistics.

## Capabilities

### Modified Capabilities
- `snapshot-history`: extends "Restore a directory at a given date" and "Interactive version
  selection when restoring" so a recursive directory restore gains its own interactive
  snapshot picker (with a non-interactive `--snapshot <id>` equivalent), instead of the
  picker being explicitly out of scope for directory restores.

## Impact

- `Vara.Cli/Commands/RestoreCommand.cs`: new `--snapshot` option, new interactive
  snapshot-picker path for `--recursive`, new picker row rendering/coloring.
- `Vara.Application/History/SnapshotHistoryService.cs`: new method(s) to enumerate
  candidate snapshots for a directory and compute their directory-scoped statistics.
- `Vara.Core/Abstractions/ISnapshotRepository.cs` and
  `Vara.Infrastructure/Snapshots/SqliteSnapshotRepository.cs`: new query for file-version
  records under a path prefix across all snapshots.
- `openspec/specs/snapshot-history/spec.md`: requirement updates described above.
