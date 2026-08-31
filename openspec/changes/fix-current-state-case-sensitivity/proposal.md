## Why

`SqliteSnapshotRepository.GetCurrentState()` builds its path-keyed manifest lookup with the
default (ordinal, case-sensitive) string comparer, while every other path-keyed collection in
the codebase (`BackupDiffer`'s scanned-paths set, `BackupPlanner`'s move-source tracking,
`YamlProfileConfigLoader`'s duplicate-name check, `GetFileHistory`'s visited-path guard) uses
`StringComparer.OrdinalIgnoreCase`. On a case-preserving filesystem, a file whose name changes
only in casing (e.g. `Photo.JPG` renamed to `photo.jpg`) fails to match its existing manifest
entry: the diff stage treats it as a brand-new file (re-hashed and re-copied every run) while
the old entry is never marked deleted, permanently pinning its content from garbage collection.

## What Changes

- `SqliteSnapshotRepository.GetCurrentState()` builds its result dictionary with
  `StringComparer.OrdinalIgnoreCase`, matching every other path-keyed collection in the codebase.
- Incremental change detection treats a source path as matching its recorded manifest entry
  case-insensitively, so a casing-only rename is recognized as the same tracked file rather than
  spuriously classified as an addition with an orphaned prior manifest row.

## Capabilities

### New Capabilities

(none)

### Modified Capabilities

- `backup-execution`: the "Incremental change detection" requirement is clarified to match
  source paths against the manifest case-insensitively, so a file whose casing changes on a
  case-preserving filesystem is not misclassified as a new addition, and its prior manifest
  entry is not left orphaned.

## Impact

- **Code**: `src/Vara.Infrastructure/Snapshots/SqliteSnapshotRepository.cs` (`GetCurrentState()`).
- **Tests**: `tests/Vara.Infrastructure.Tests/Snapshots/SqliteSnapshotRepositoryTests.cs`,
  `tests/Vara.Application.Tests/Backup/BackupExecutorTests.cs` (regression coverage for a
  casing-only rename across a backup run).
- **No schema/migration changes** - the fix is purely in how the existing manifest rows are
  looked up in memory; no on-disk format changes.
