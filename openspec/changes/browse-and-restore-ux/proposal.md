## Why

The CLI is the only way for a user to browse and recover past versions of their files, but the
current commands assume the user already knows a file's exact mirror-relative path, its profile
name, and a precise `--version`/`--at` value before they can see or recover anything. There is no
way to browse what a directory (or an entire profile) looked like at a point in time, no way to
discover what was deleted, and `restore` always requires a separate `--out` destination even when
the user just wants their original file back. These gaps make "time travel" harder than it should
be for the tool's primary purpose.

## What Changes

- `history`, `restore`, and the new `show`/`diff`/`browse`/`deleted` commands accept a path
  expressed as a mirror-relative path (today's only supported form), an absolute source path, or a
  path relative to the current working directory - resolved by one shared rule, trying an exact
  literal match against recorded history first (preserving today's behavior) before falling back
  to resolving the input against the working directory.
- **BREAKING**: the profile name changes from a required positional argument to a `--profile`
  option, consistently across every command (`backup`, `snapshots`, `history`, `restore`,
  `prune`, and the new `browse`/`deleted`/`show`/`diff`). This was needed because
  `System.CommandLine` cannot cleanly bind a single positional token to a later, still-required
  positional argument when an earlier one becomes optional - see design.md's "CLI argument shape"
  decision. Since this project has no external users yet, there is no migration concern.
- `--profile` becomes optional specifically for the read-only/browsing commands (`snapshots`,
  `history`, `restore`, `show`, `diff`, `browse`, `deleted`) when the current working directory
  resolves to a profile's target root; it remains required for the mutating commands (`backup`,
  `prune`).
- The profile name argument becomes optional for these commands when the current working
  directory resolves to a profile's target root by walking upward for a `.vara\profile.db` (the
  same way `git` locates `.git`), without needing `~/.vara/profiles.yml` at all in that case.
- New `browse` command: lists the immediate (one-level) contents of a mirror directory, either as
  it looked at a given time (`--at <date>`) or as it stands now, optionally including deleted
  entries (`--deleted`) interleaved with live ones and visually marked, so a user can navigate the
  backup like a filesystem to find something to restore.
- New `deleted` command: a profile-wide (or subtree-scoped) report of deleted files, most recently
  deleted first, for when a user knows something is missing but not where it lived.
- New `show` command: streams a specific version's content straight to stdout (useful for text
  files; binary content prints as raw bytes, same as `cat` would), without writing anything to
  disk.
- New `diff` command: shows a textual diff between two versions of the same path (by version id
  and/or date).
- `restore` gains an `--in-place` flag, mutually exclusive with `--out`, that writes the restored
  content back to its original absolute source location (reversing the existing mirror-path
  transform) instead of requiring an explicit destination. It reuses the existing overwrite
  confirmation guard unchanged: no prompt when nothing exists there yet (e.g. undeleting a file),
  a prompt/`--force` when it would overwrite a live file.
- `restore` gains an interactive version picker: when invoked without `--version` or `--at` in an
  interactive session, it shows a selectable version history (reusing `history`'s rendering) so
  the user can pick a version rather than having to already know its id or date. Non-interactive
  sessions still require `--version` or `--at` explicitly.

## Capabilities

### New Capabilities
- `backup-browsing`: one-level directory listing of a profile's mirror (live and/or deleted
  entries, at a given time or now) and a profile-wide "recently deleted" report, so a user can
  navigate the backup like a filesystem to find something to restore.

### Modified Capabilities
- `snapshot-history`: adds the shared flexible path-resolution rule used by `history`, `restore`,
  and the new `show`/`diff` commands; adds `restore --in-place`; adds `restore`'s interactive
  version picker; adds the new `show` and `diff` commands.
- `profile-config`: the profile name argument becomes optional for browsing/restoring commands
  when the current working directory resolves to a profile's target root; it remains required
  otherwise.

## Impact

- `src/Vara.Cli/Commands/`: `HistoryCommand.cs` and `RestoreCommand.cs` gain the new path
  resolution and (for restore) the `--in-place`/interactive-picker flow; new `BrowseCommand.cs`,
  `DeletedCommand.cs`, `ShowCommand.cs`, `DiffCommand.cs`.
- `src/Vara.Application/Profiles/ProfileResolver.cs`: new upward-search resolution when no
  profile name is given.
- `src/Vara.Application/History/SnapshotHistoryService.cs`: new members backing directory
  listing, deleted-entry reporting, content streaming/diffing, and in-place restore's reverse
  path mapping.
- `src/Vara.Core/Abstractions/ISnapshotRepository.cs` and
  `src/Vara.Infrastructure/Snapshots/SqliteSnapshotRepository.cs`: new queries for "state as of a
  given time," "current tombstones," and "tombstones as of a given time," scoped to a directory
  prefix or the whole profile.
- `src/Vara.Core/Abstractions/IContentStore.cs` and
  `src/Vara.Infrastructure/Storage/FileSystemContentStore.cs`: a new read-stream primitive for
  `show`/`diff` (today only `ExtractTo`, which writes to a destination file, exists).
- `src/Vara.Infrastructure/FileSystem/AbsolutePathMirrorMapper.cs`: gains the reverse mapping
  (mirror-relative -> original absolute source path) needed by `--in-place` and by resolving
  absolute-source-path input.
- `src/Vara.Cli/Presentation/`: new presenter(s) for directory listings and deleted reports,
  reusing the existing change-kind color convention; `Program.cs` registers the new commands.
- No manifest schema, storage layout, or backup/prune behavior changes.
