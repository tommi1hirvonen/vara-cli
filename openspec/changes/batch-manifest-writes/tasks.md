## 1. Manifest connection settings

- [ ] 1.1 Set `PRAGMA journal_mode=WAL` and `PRAGMA synchronous=NORMAL` when `SqliteSnapshotRepository` opens its connection, and verify with a unit test in `Vara.Infrastructure.Tests` that querying `PRAGMA journal_mode` on the opened connection returns `wal`.
- [ ] 1.2 Add a `PRAGMA wal_checkpoint(TRUNCATE)` call executed after a snapshot's batch transaction commits, and verify with a test that the checkpoint reports a clean state (no pending WAL frames) after a completed run.

## 2. Batch transaction API on the repository

- [ ] 2.1 Add an explicit batch-scope mechanism to `SqliteSnapshotRepository` (e.g. a `BeginManifestBatch()` method returning an `IDisposable`/commit handle) that lets a caller wrap multiple `RecordFileVersion` calls in one transaction, following the existing `BeginTransaction()`/`Commit()` pattern already used in `DeleteSnapshot`/`PruneSnapshots`, and verify with a unit test that no row exists in `file_versions` before the batch commits and all expected rows exist immediately after commit.
- [ ] 2.2 Route `CompleteSnapshot`/`FailSnapshot` through the same open batch transaction when one is active, so a snapshot's outcome commits atomically with its rows, and verify with a unit test that a simulated failure before commit leaves neither the rows nor the outcome update visible.
- [ ] 2.3 Keep `BeginSnapshot` as its own auto-committed statement outside any batch scope, and verify with a unit test that a snapshot row is visible/queryable (e.g. via `ReconcileIncompleteSnapshots`) immediately after `BeginSnapshot` returns, even before any batch commits.

## 3. BackupExecutor integration

- [ ] 3.1 Wrap `BackupExecutor.Execute`'s manifest writes (the Move/Delete loop, the parallel transfer loop, and the trailing `CompleteSnapshot`/`FailSnapshot` call) in a single batch scope per snapshot, and verify with a test in `Vara.Application.Tests` that a simulated mid-run failure before commit leaves the mirror content already written (from `IContentStore`) untouched while no manifest rows for that snapshot are visible.
- [ ] 3.2 Confirm the existing `reportLock` serialization still guards all manifest calls made from parallel workers within the batch, and verify with a concurrency test (many parallel Add operations against a fake/in-memory repository or a real SQLite file) that all expected rows are present after the batch commits, with no lost or duplicate rows.

## 4. Regression and behavior tests

- [ ] 4.1 Add/update `SqliteSnapshotRepositoryTests` to cover batched-commit visibility (2.1), atomic outcome commit (2.2), the WAL pragma settings (1.1), and checkpoint behavior (1.2).
- [ ] 4.2 Add/update the `BackupExecutor` test suite to cover the new "Manifest writes are batched per snapshot" requirement's two scenarios: a many-small-files run completing without a per-file commit, and a simulated mid-batch interruption where the mirror is left correct and the next run's incremental scan re-detects and re-records the affected files without error.
- [ ] 4.3 Run `dotnet test` for the full solution and verify all tests pass.

## 5. Documentation

- [ ] 5.1 Update the XML doc comments on `SqliteSnapshotRepository` and `BackupExecutor` (and the class-level `BackupExecutor` summary describing manifest writes/locking) that currently describe per-call commit behavior, so they reflect the new batched-transaction behavior, and verify by reviewing the diff for any remaining stale comments.
