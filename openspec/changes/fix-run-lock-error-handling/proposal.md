## Why

`FileRunLock`'s concurrency lock is correctly scoped per-target (it ignores which profile is asking), but the error messages built on top of it - `BackupAlreadyRunningException` and `PruneAlreadyRunningException` - name only the requesting profile ("A backup run for profile 'X' is already in progress"). When two profiles share the same target root, the message a blocked profile sees implies that profile itself is the one already running, which is misleading: the actual holder could be a different profile entirely. Separately, `FileRunLock.TryAcquire` only catches `IOException` when opening the lock file; if the `.vara` directory or lock file is inaccessible (for example, due to filesystem permissions), the resulting `UnauthorizedAccessException` escapes `TryAcquire` unhandled instead of producing a clear, actionable error.

## What Changes

- Reword the "already in progress" messages (used by both the backup and prune commands) to describe the shared target being busy, rather than implying the requesting profile itself is the one holding the lock.
- `FileRunLock.TryAcquire` now catches `UnauthorizedAccessException` in addition to `IOException`, surfacing it as a distinct, clear error (permission denied acquiring the run lock) instead of letting an unhandled exception propagate.
- No change to the lock's actual scope or semantics: it remains correctly keyed per-target, not per-profile.

## Capabilities

### New Capabilities
(none)

### Modified Capabilities
- `backup-execution`: the "Concurrent run prevention" requirement's reported message no longer implies the blocked profile itself holds the lock, and a lock file access failure now produces a clear error instead of propagating unhandled.

### New requirement in existing capability
- `retention-pruning`: adds a "Concurrent run prevention" requirement documenting the same shared-target lock behavior for `vara prune`, which previously used the same lock but had no corresponding spec requirement.

## Impact

- `src/Vara.Infrastructure/Concurrency/FileRunLock.cs` (catch `UnauthorizedAccessException`)
- `src/Vara.Core/Backup/BackupExceptions.cs`, `src/Vara.Core/Backup/PruneExceptions.cs` (reworded messages)
- New exception type for the lock-file access-denied case (exact location decided in design.md)
- `tests/Vara.Infrastructure.Tests/Concurrency/FileRunLockTests.cs`, exception-message assertions in backup/prune tests
