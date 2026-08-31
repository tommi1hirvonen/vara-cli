## Why

`SqliteSnapshotRepository.RecordFileVersion` never sets `journal_mode`/`synchronous`, and every call is its own auto-committed (fsync'd) insert. `BackupExecutor` serializes these calls under a single lock across the parallel transfer executor, so for many-small-file backups the fsync-per-file manifest write becomes the real bottleneck - likely slower than the file copies themselves. `design.md` for the initial implementation already flagged this risk and proposed "batch per snapshot where safe" as the mitigation, but the implementation never did it.

## What Changes

- Wrap a snapshot's manifest writes (`RecordFileVersion` calls, and the move/delete pair) in an explicit transaction spanning the snapshot (or safe chunks of it), instead of one auto-committed insert per call.
- Enable `journal_mode=WAL` and `synchronous=NORMAL` on the manifest connection to reduce per-commit fsync cost while keeping crash durability.
- Document and accept the resulting bounded loss window: if the process crashes mid-batch, the in-flight transaction rolls back, so manifest rows for files already copied to the mirror in that batch can be lost even though the mirror content itself is correct. The next run's incremental scan has no manifest record for those paths, so it re-adds them - redundant work, not data loss or corruption.

## Capabilities

### New Capabilities
(none)

### Modified Capabilities
- `backup-execution`: add a requirement describing manifest write batching and the crash-recovery behavior it implies (bounded per-batch durability window, self-healing re-add on the next run) - the current "Crash and interruption safety" requirement covers mirror content but says nothing about how much in-flight manifest history a crash may lose.

## Impact

- `src/Vara.Infrastructure/Snapshots/SqliteSnapshotRepository.cs`: add PRAGMA setup on connection open; add an explicit transaction/batch boundary around per-snapshot manifest writes.
- `src/Vara.Application/Backup/BackupExecutor.cs`: begin the batch transaction before executing a snapshot's operations and commit it after, instead of relying on per-call auto-commit; the existing `reportLock` serialization is unaffected.
- Tests: `tests/Vara.Infrastructure.Tests/Snapshots/SqliteSnapshotRepositoryTests.cs`, `tests/Vara.Application.Tests` (BackupExecutor) - add coverage for batched commit behavior and for a simulated mid-batch crash leaving the mirror correct and the manifest self-healing on the next run.
