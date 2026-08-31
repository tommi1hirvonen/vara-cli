## Why

`restore --out` extracts historical file content via `File.Copy(..., overwrite: true)` with no
check on the destination: an existing file at `--out` is silently clobbered, and nothing stops
`--out` from resolving inside the profile's own live mirror (`TargetRoot`), which would corrupt
the very data restore is meant to recover from. Restore is documented as extracting content
"without touching the live mirror," but that invariant is currently unenforced.

## What Changes

- `restore` refuses to write to a destination that resolves inside the profile's live mirror
  (`TargetRoot`), unconditionally - this is a hard error, not overridable by any flag.
- `restore` refuses to overwrite an existing destination file unless a new `--force` option is
  passed. **BREAKING**: a script that previously relied on `restore --out <existing-file>`
  silently overwriting must now pass `--force`.
- When the destination exists, `--force` is not given, and the process is running interactively
  (stdin is not redirected), `restore` prompts for confirmation (default: no) before overwriting.
- When the destination exists, `--force` is not given, and stdin is redirected (non-interactive),
  `restore` fails immediately with a clear error directing the user to `--force`.
- Declining the confirmation prompt cancels the restore cleanly and exits with code 0 (a
  deliberate no-op, not a failure).

## Capabilities

### New Capabilities
(none)

### Modified Capabilities
- `snapshot-history`: the restore requirement gains destination-safety guarantees - refusing to
  write inside the live mirror, and requiring explicit confirmation (via `--force` or an
  interactive prompt) before overwriting an existing destination file.

## Impact

- `src/Vara.Cli/Commands/RestoreCommand.cs`: new `--force` option, stdin-redirect detection, and
  the interactive overwrite prompt.
- `src/Vara.Application/History/SnapshotHistoryService.cs`: `RestoreAsOf`/`RestoreVersion` gain
  the mirror-containment and overwrite guards, throwing new domain exceptions.
- `src/Vara.Core/Abstractions/IContentStore.cs`: new predicate members so the content store (the
  only component that already knows the mirror root) can answer "is this path inside the mirror"
  and "does this destination already exist."
- `src/Vara.Infrastructure/Storage/FileSystemContentStore.cs`: implements the new predicates.
- `src/Vara.Core/Snapshots/SnapshotExceptions.cs`: two new exception types.
- `src/Vara.Cli/Composition/ErrorReporting.cs`: registers the new exceptions for friendly
  reporting.
- `tests/Vara.Application.Tests/History/SnapshotHistoryServiceTests.cs` and
  `tests/Vara.Application.Tests/Backup/Fakes.cs` (`FakeContentStore`): new coverage and the
  interface additions.
- No manifest schema, storage layout, or backup/prune behavior changes.
