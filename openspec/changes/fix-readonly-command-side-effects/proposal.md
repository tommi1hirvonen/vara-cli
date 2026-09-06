## Why

`snapshots` and `browse` are documented and intended as read-only commands, but both resolve their
profile through `ProfileServiceFactory.CreateFor`, which constructs a `SqliteSnapshotRepository` and
a `FileSystemContentStore`. Both constructors unconditionally create filesystem state as a side
effect: `SqliteSnapshotRepository` creates its containing directory and opens (thus creates) the
SQLite file at `.vara\profile.db`, and `FileSystemContentStore` creates the mirror, versions, and
temp directories. Running a purely informational command against a profile that was never backed
up (for example, to check "has this ever run?") silently creates an empty `.vara\` structure as a
side effect, which is surprising for a read-only operation and can mask a legitimate "nothing here
yet" state.

## What Changes

- Read-only commands (`snapshots`, `browse`, and any other command that only reads state) no longer
  create the profile's `.vara\` directory structure or an empty `profile.db` as a side effect of
  resolving their services.
- When a read-only command targets a profile with no existing recorded state, it reports a clear
  "nothing recorded yet" outcome instead of (or in addition to) silently materializing empty backing
  files.
- Commands that legitimately need to create this state (`backup`, and anything that writes) are
  unaffected and continue to create it as needed.

## Capabilities

### New Capabilities

(none)

### Modified Capabilities

- `backup-browsing`: the "One-level directory listing" requirement is clarified so that browsing a
  profile does not itself create backing storage as a side effect.
- `snapshot-history`: the "List snapshots" requirement is clarified so that listing snapshots does
  not itself create backing storage as a side effect.

## Impact

- **Code**: `src/Vara.Cli/Composition/ProfileServiceFactory.cs`,
  `src/Vara.Infrastructure/Snapshots/SqliteSnapshotRepository.cs` (constructor),
  `src/Vara.Infrastructure/Storage/FileSystemContentStore.cs` (constructor), and the `snapshots`/
  `browse` command handlers that call the factory.
- **Tests**: coverage asserting `snapshots`/`browse` against a never-backed-up profile leaves no
  `.vara\` directory behind.
- Design work needed on exactly where to draw the "read-only vs. write" line without changing
  `backup`'s existing eager-creation behavior - see design.md.
