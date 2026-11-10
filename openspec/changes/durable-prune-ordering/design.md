## Context

See proposal.md - Why. This section holds what an implementer in a fresh session needs.

- `PruneRun.execute` (`src/Vara.Engine/Runs/PruneRun.fs`) calls `SqliteManifest.removeSnapshots`, then lists `ContentStore.storedHashes` minus `SqliteManifest.referencedHashes`, then deletes each blob with `ContentStore.deleteBlob`.
- `SqliteManifest.openAt` sets `journal_mode = wal` and `synchronous = normal`. In WAL mode with NORMAL, SQLite syncs the log only at a checkpoint, so a transaction that has returned from `Commit()` can still be lost in a power failure (the database stays consistent, but the commit is gone).
- `SqliteManifest.checkpoint` (used by backups) commits and then runs `PRAGMA wal_checkpoint(TRUNCATE)`, so the database file is whole between runs. `removeSnapshots` commits its own transaction and does neither.
- Tests are named after the spec: a module named after the requirement title, a test named after the scenario title (F# double-backtick names). `tests/Vara.Testing/SpecCoverage.fs` fails the build of the test run when a scenario of a *main* spec has no test, which applies once this change is synced or archived. Write the tests with the exact scenario titles from `specs/retention-pruning/spec.md`.

## Goals / Non-Goals

**Goals:**
- The record removal is durable before the first `DeleteBlob`.
- A test fails against the current code and passes after the change.

**Non-Goals:**
- Changing the manifest's global `synchronous` setting. Backups rely on NORMAL for throughput, and their own durability story is the subject of the separate change `durable-blob-store`.
- Making blob deletion itself durable. A blob deleted but resurrected by a crash is an orphan the next prune removes.
- Any change to what a prune removes, to confirmation, cancellation or progress.

## Decisions

**Raise the commit to `synchronous = FULL` for the removal transaction, and run `PRAGMA wal_checkpoint(TRUNCATE)` after it.** FULL is the documented guarantee that a commit is durable when it returns; it covers the case where the checkpoint cannot finish because another process holds a read transaction (the busy timeout is finite). The truncating checkpoint is what `checkpoint` already does for backups and keeps the database file whole between runs, and it is the part a test can observe (see below). The setting is restored to NORMAL in a `finally`, because the same connection continues to read afterwards. `PRAGMA synchronous` cannot change inside a transaction, so it is set before `BeginTransaction`.
Alternatives considered: only the checkpoint (simpler, but durability then depends on the checkpoint not being blocked); only FULL (documented, but nothing observable in a test, and the file is not whole afterwards); changing `openAt` to FULL for every run (slows every backup commit for no benefit to them).

**Share the WAL-folding statement with `checkpoint`.** Extract a small private function used by both, so the two places cannot drift. This is the only repetition worth removing here.

**Test technique.** A power loss that discards everything not yet flushed is simulated by copying only the main database file (not its `-wal`) and reading the copy. Do the copy from inside the `DeleteBlob` effect of `PruneEnv`, which a test supplies as a function: at the first call the copy must already show the snapshots gone; after some blobs are deleted, no row in the copy may reference a blob that has been deleted. Before the change, the copy still contains the removed snapshots' rows while their blobs are gone, so the test fails. `File.Copy` works while the connection is open because SQLite opens the file with read sharing.

## Risks / Trade-offs

- [One more log sync and a checkpoint per prune that removes snapshots] → Prune is an occasional manual command; the cost is milliseconds.
- [A checkpoint that cannot truncate because a reader is active] → Not an error: the commit is already durable through FULL, and the truncate is cosmetic. Do not check or throw on the pragma's busy result.
- [The test proves the main file is whole, not that the operating system synced it] → Accepted; the fsync itself is SQLite's documented behaviour under FULL, and cannot be asserted from a unit test.
