## Context

See proposal.md - Why. `SqliteSnapshotRepository` opens a single `SqliteConnection` per profile and every mutating method (`RecordFileVersion`, `BeginSnapshot`, `UpdateSnapshotOutcome`, ...) runs as its own implicit auto-committed transaction under default SQLite settings (rollback-journal mode, `synchronous=FULL`). `BackupExecutor` calls `RecordFileVersion` once per Add/Change/Move/Delete operation, serialized under `reportLock` across a `Parallel.ForEach` of transfer workers - so N worker threads fan in to one lock, and each pass through it pays a full fsync. `DeleteSnapshot`/`PruneSnapshots` already demonstrate the codebase's existing pattern for explicit multi-statement transactions (`_connection.BeginTransaction()` + `transaction.Commit()`), so batching manifest writes follows an established idiom rather than introducing a new one.

## Goals / Non-Goals

**Goals:**
- Reduce manifest write commit overhead from O(files changed) fsyncs to a small, bounded number of fsyncs per snapshot.
- Preserve the existing "Crash and interruption safety" guarantee for mirror content - batching manifest commits must never leave the mirror or the manifest schema corrupted.
- Keep the change confined to `SqliteSnapshotRepository` and the call site in `BackupExecutor`; no schema changes.

**Non-Goals:**
- Not changing the on-disk schema (`snapshots`/`file_versions` tables/columns are unchanged).
- Not making manifest writes safe for multiple concurrent processes/connections - `ISnapshotRepository` is still used by a single process per profile (see "Concurrent run prevention"), so no cross-process locking design is needed here.
- Not attempting perfect zero-loss durability on crash - some redundant re-recording after an interruption is an accepted trade-off (see Risks).

## Decisions

**Batch granularity: one transaction per snapshot, not per N rows.**
A whole-snapshot transaction is the simplest option that matches design.md's original "batch per snapshot where safe" language, and matches how `BackupExecutor.Execute` is already scoped (one call, one snapshot). Chunking into fixed-size sub-batches (e.g. every 500 rows) would bound the crash-loss window more tightly, but adds a counter/threshold and a mid-run commit-and-reopen dance for marginal benefit, since the loss window in either case only causes redundant re-copying, not corruption or data loss (see Risks). Alternative considered and rejected: leave writes uncommitted only during the parallel transfer phase and commit before `CompleteSnapshot`/`FailSnapshot` runs - this is effectively the same as "one transaction per snapshot" but described differently, so it collapses into the same design.

**Transaction scope covers Move/Delete and Add/Change alike.**
`BackupExecutor.Execute` begins the transaction before the sequential Move/Delete loop and commits it after the parallel transfer loop, wrapping `CompleteSnapshot`/`FailSnapshot` as well so the whole snapshot's outcome (rows + status) commits atomically. This keeps `BeginSnapshot` (which must be visible immediately so `ReconcileIncompleteSnapshots` can find it as `Running` after a crash) as its own separate auto-committed statement, outside the batch transaction.

**PRAGMA `journal_mode=WAL` + `synchronous=NORMAL`.**
WAL mode lets readers and the single writer proceed without blocking each other and reduces fsync cost per commit; `synchronous=NORMAL` under WAL still guarantees the database itself is never corrupted on crash (SQLite's documented guarantee), only that a very recent commit might not survive a power loss (acceptable here, since a batch's commit boundary already tolerates a wider loss window than that). `synchronous=FULL` was rejected as unnecessary given the batching already bounds the exposure. Rollback-journal mode was rejected because it exclusively locks the whole database file for the duration of a write transaction, which is unnecessary here and less efficient than WAL for this access pattern.

**Checkpoint WAL back into the main database file before returning from a completed run.**
WAL mode leaves recent commits in a separate `-wal` file alongside the main `.db` file; a tool that copies only the main file (e.g. a naive external backup of the profile's own state directory) could see a stale, though never corrupted, snapshot of the manifest. Running `PRAGMA wal_checkpoint(TRUNCATE)` once a snapshot's transaction commits folds the WAL back into the main file and truncates it, so the on-disk `.db` file is self-contained again between runs.

**`RecordFileVersion` and friends keep their existing method signatures.**
The repository already exposes `BeginSnapshot`/`CompleteSnapshot`/`FailSnapshot` as natural batch boundaries; batching is implemented by adding explicit begin/commit calls (or an `IDisposable` batch-scope helper) around the existing per-row calls, not by changing `RecordFileVersion`'s signature or introducing a bulk-insert API. This keeps the change surgical and keeps `BackupExecutor`'s per-operation call sites unchanged except for the added transaction scope.

## Risks / Trade-offs

- [Risk] A crash mid-batch rolls back manifest rows for files whose content was already written to the mirror in that batch → Mitigation: this is self-healing, not data loss - `ReconcileIncompleteSnapshots` marks the interrupted snapshot `Failed` on next startup, and the next run's incremental scan finds no manifest record for the affected paths and simply re-adds them (redundant copy, not corruption). This trade-off is explicit in the new backup-execution requirement.
- [Risk] WAL mode leaves a `-wal`/`-shm` sidecar file next to the manifest database; a naive file copy of just the `.db` file mid-run (or before a checkpoint) could look stale to an external tool → Mitigation: checkpoint-and-truncate the WAL after each snapshot completes, so the plain `.db` file is self-contained at rest between runs.
- [Trade-off] Whole-snapshot batching (vs. finer chunking) trades a larger potential redundant-recopy window for simplicity → accepted, since the failure mode is bounded to "redundant work on the next run," not correctness.
- [Risk] Holding one open write transaction for the full duration of a large snapshot's transfer phase increases the amount of dirty WAL data before a checkpoint → Mitigation: no other process/connection writes to this database concurrently (single profile, single active run - see "Concurrent run prevention"), so there is no contention to manage; only steady-state disk space for the WAL file is a minor consideration, resolved by the post-snapshot checkpoint.

## Migration Plan

No schema migration - table/column definitions are unchanged. The only durable on-disk effect is that `PRAGMA journal_mode=WAL` persists in the database file's header the first time an existing profile's manifest is opened after this change ships, so existing manifests silently switch from rollback-journal to WAL on their next use; no user action is required, and both modes read/write the same table contents. Rollback is a code revert (drop the PRAGMA calls and transaction wrapping); a manifest already switched to WAL continues to work correctly against the reverted code (SQLite transparently supports opening a WAL-mode file with default settings), so no data migration is needed in either direction.
