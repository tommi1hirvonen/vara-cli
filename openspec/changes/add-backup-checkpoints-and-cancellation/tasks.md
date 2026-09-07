## 1. Manifest checkpointing

- [ ] 1.1 Change `IManifestBatch.Commit()`'s contract to allow repeated calls (each durably committing writes so far and continuing to accept more), and update its XML doc accordingly; verify `Vara.Core.Tests` compiles against the updated interface
- [ ] 1.2 Update `SqliteSnapshotRepository`'s `CommitActiveBatch` path so a checkpoint commit commits and checkpoints the WAL, then immediately reopens a new `SqliteTransaction` on the same connection instead of clearing `_activeBatchTransaction`; verify a unit test can call `Commit()` twice on the same `IManifestBatch` and see both sets of writes durable
- [ ] 1.3 Add a time-based checkpoint trigger (default 30s interval, single named constant) checked in `BackupExecutor` after each completed operation (Move/Delete/Add/Change) under the existing `reportLock`, calling `Commit()` on the active batch and resetting the interval clock when elapsed; verify a test with a short interval override observes multiple commits during one simulated run
- [ ] 1.4 Verify via `Vara.IntegrationTests` that interrupting a run (simulated by throwing after N checkpoints) leaves only the post-last-checkpoint files undetected by the next run's incremental diff, not the whole run

## 2. Cancellation signal

- [ ] 2.1 Add a cooperative cancellation signal (e.g. a `CancellationToken`/`CancellationTokenSource` used only to mean "stop starting new work") threaded into `BackupPipeline.Run` and `BackupExecutor.Execute`, checked before dispatching each new Move/Delete/Add/Change operation in both the sequential loop and the `Parallel.ForEach`; verify a unit test that cancels mid-run completes only in-flight operations and starts no new ones
- [ ] 2.2 Register a `Console.CancelKeyPress` handler in `Vara.Cli.Program` that sets `ConsoleCancelEventArgs.Cancel = true` on first invocation and signals the cooperative cancellation source, and calls `Environment.Exit` immediately on a second invocation; verify a `Vara.Cli.Tests` test exercises both the first- and second-press code paths
- [ ] 2.3 On a first Ctrl+C, force an out-of-cycle checkpoint commit covering all work completed so far (reusing the mechanism from task 1.2/1.3) before the run exits; verify a test asserts the forced checkpoint's rows are durable even though the periodic interval had not yet elapsed

## 3. Cancelled snapshot status

- [ ] 3.1 Add `Cancelled` to `SnapshotStatus` in `Vara.Core.Snapshots.Snapshot`; verify the solution builds and existing `nameof`-based persistence round-trips the new value in a `Vara.Infrastructure.Tests` test
- [ ] 3.2 Wire `BackupPipeline.Run` so a graceful (first-Ctrl+C) stop calls the equivalent of `FailSnapshot`/`CompleteSnapshot` but records status `Cancelled`, committed as part of the forced checkpoint from task 2.3; verify a test confirms the snapshot row is `Cancelled`, not left `Running`, after a graceful stop
- [ ] 3.3 Confirm `ReconcileIncompleteSnapshots` behavior is unchanged (still flips any still-`Running` snapshot to `Failed`), so a second-Ctrl+C or non-Ctrl+C kill continues to be reconciled as `Failed`; verify with an existing or new `Vara.Infrastructure.Tests` test

## 4. Snapshot history display

- [ ] 4.1 Map `SnapshotStatus.Cancelled` to the existing "partial success" (warning) severity style in the snapshot listing/history display code, distinct from `Failed`'s hard-error style; verify a `Vara.Cli.Tests` test renders a `Cancelled` row in the warning style and a `Failed` row in the hard-error style
- [ ] 4.2 Verify `vara snapshots`/history output for a profile containing a `Cancelled` snapshot displays it distinguishably from `Failed` and `Complete` rows in an end-to-end/integration test

## 5. Regression checks

- [ ] 5.1 Run the full test suite (`dotnet test`) and confirm all existing backup-execution, snapshot-history, and manifest-batching tests still pass unmodified in intent (only updated where this change's specs required it)
