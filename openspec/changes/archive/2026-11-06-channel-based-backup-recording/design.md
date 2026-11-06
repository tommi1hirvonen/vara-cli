## Context

See proposal.md - Why. Relevant current state of `BackupExecutor.Execute`, which is synchronous and called by `BackupPipeline.Run` on the pipeline thread:

1. A sequential pass on the calling thread runs Move/Delete/Link operations and records them under `reportLock`.
2. `onTransferPhaseStarting` fires.
3. `Parallel.ForEach` runs Add/Change transfers with `MaxDegreeOfParallelism = transfer_concurrency` (default 1). The calling thread participates as a worker. Each worker:
   - calls `onFileStarted`;
   - streams into the content store, outside the lock, with chunk progress via `onBytesTransferred`;
   - re-stats the source for torn-read detection;
   - places the file at the mirror path;
   - under `reportLock`, calls `RecordFileVersion`, updates `Counts`, and calls `CheckpointIfDue`;
   - then calls `onFileFinished` (in `finally`) and, on success, `onFileTransferred`.
4. Cooperative cancellation is an `IsCancellationRequested` check at the start of each iteration, not `ParallelOptions.CancellationToken`, so no `OperationCanceledException` is thrown.
5. `IOException`/`UnauthorizedAccessException` become a failed-path entry. Any other exception propagates out of `Parallel.ForEach` and is handled by `BackupPipeline.Run`'s catch, which records `FailSnapshot`, commits, and rethrows.

`IManifestBatch.Commit` and `ISnapshotRepository` share one SQLite connection that is not safe for concurrent use.

## Goals / Non-Goals

**Goals:**
- Every `ISnapshotRepository` / `IManifestBatch` call made by the executor happens on the thread that called `Execute`.
- No lock in `BackupExecutor`; counters, `failedPaths`, and the checkpoint clock have exactly one owner.
- Identical observable outcomes for success, per-file failure, torn reads, graceful cancellation, and unexpected failures, as covered by the `backup-execution` scenarios.

**Non-Goals:**
- Making the executor or pipeline asynchronous, or changing `Execute`'s signature.
- Changing `BackupPlanner`'s parallel hashing (a plain parallel map with no shared mutable state).
- Changing checkpoint cadence, the default transfer concurrency, or progress-callback semantics beyond the ordering note in Decision 4.

## Decisions

### 1. Workers run off-thread; the calling thread becomes the recorder

`Execute` starts the transfer loop (`Parallel.ForEach`, same `MaxDegreeOfParallelism`, same per-iteration cancellation check) inside `Task.Run`, then runs the recorder loop on its own thread until the results channel completes. This is the inverse of today, where the calling thread is one of the workers and SQLite access hops between threads under the lock.

**Alternatives considered:**
- Run the recorder on a background task and keep the calling thread as a worker. Rejected: it would move SQLite access off the thread that calls `BeginSnapshot` and `CompleteSnapshot`, which is the opposite of the goal.
- Make `Execute` async with `Parallel.ForEachAsync`. Rejected: async would ripple through `BackupPipeline` and every CLI handler for no behavioral gain.

### 2. Unbounded, single-reader channel of immutable per-file results

The results channel is created with `Channel.CreateUnbounded<TransferResult>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = false })`. `TransferResult` is a private closed hierarchy, consistent with the codebase's C# 15 modelling:
- `Transferred(RelativePath, Hash, Size, SourceModifiedAt, ChangeKind, QuickHash)`
- `Failed(RelativePath)`

Workers publish with `TryWrite`, which always succeeds on an unbounded channel that is still open, so workers never block on the recorder.

**Why unbounded:** a result is a few small fields. The recorder's per-result work (one prepared insert inside an open transaction, plus a clock check) is far cheaper than a file transfer, so the backlog stays tiny in practice. A bounded channel would need a blocking write from `Parallel.ForEach` workers, which means sync-over-async on thread-pool threads.

**Alternative considered:** `BlockingCollection<T>`. It would also work, but a `Channel` gives completion-with-exception semantics (Decision 5) without a separate error side-channel.

### 3. The recorder reads synchronously on the calling thread

The recorder loops on `reader.WaitToReadAsync().AsTask().GetAwaiter().GetResult()` and drains with `TryRead`. Blocking here is acceptable: the calling thread is the pipeline's own thread, not a thread-pool thread, and console apps have no `SynchronizationContext` to deadlock on. This blocking wait is the only sync-over-async point in the change and is confined to one method.

For each result, the recorder runs exactly what the worker's `lock` body does today:
- `Transferred`: `RecordFileVersion`, then counters, then `CheckpointIfDue`, then `onFileTransferred`;
- `Failed`: `failedPaths` and the `Failed` count, then `CheckpointIfDue`.

The Move/Delete/Link pass runs before the recorder starts, on the same thread, and updates the same state directly.

### 4. `onFileFinished` fires before the result is published

A worker's sequence becomes: `onFileStarted` → transfer → torn-read check → mirror placement → `onFileFinished` (in `finally`) → publish the result (unless an unexpected exception is propagating). `onFileTransferred` moves to the recorder.

Today's order is record → finished → transferred. The new order is finished → record → transferred. "Finished happens before transferred" still holds, because publishing happens-before consumption. A file now leaves the active-transfer list as soon as its I/O completes rather than after its manifest row is written. That is consistent with "active transfers" meaning I/O in progress, and no spec scenario depends on the old order.

`onBytesTransferred` and `onFileStarted` stay on the workers, unchanged.

### 5. Failure semantics mirror today's

- **Per-file I/O failure or torn read:** the worker publishes `Failed`, so the run continues. This is unchanged.
- **Unexpected worker exception:** the transfer task's wrapper catches it, completes the writer with `Complete(exception)`, and rethrows. `Parallel.ForEach` lets in-flight iterations finish, and their results are still published. The recorder drains and records every result already in the channel. When the channel reports completion, `Execute` rethrows the transfer task's original exception (`transferTask.GetAwaiter().GetResult()`), which is the same `AggregateException` shape as today. `BackupPipeline` then calls `FailSnapshot` exactly as before.
- **Recorder exception** (for example `RecordFileVersion` or `Commit` throwing): the recorder cancels a private abort `CancellationTokenSource` that is passed as `ParallelOptions.CancellationToken`, so no new iterations start. It waits for the transfer task to finish, ignoring its resulting `OperationCanceledException`, and then rethrows its own exception. Waiting first guarantees that no worker is still writing to the content store when `BackupPipeline` records the failure. Results left in the channel are discarded, and the crash/interruption-safety guarantees cover them exactly as they cover an interrupted run.
  - Accepted difference: the recorder exception now reaches `BackupPipeline` unwrapped rather than inside an `AggregateException`. Both fall into the same generic catch, and `src/` never inspects `AggregateException`, so no specified behavior changes.

### 6. Graceful cancellation drains before returning

The Ctrl+C token keeps driving only the per-iteration "do not start" check (never the abort token), so in-flight transfers finish and publish their results. The writer completes when `Parallel.ForEach` returns, and the recorder exits only after draining the channel. As a result, `Execute` returns only once every completed transfer is recorded, so the pipeline's forced checkpoint and `CancelSnapshot` cover all completed work, as the "Graceful cancellation via Ctrl+C" requirement demands.

## Risks / Trade-offs

- [Recorder lag means a crash can leave content already placed in the mirror without its manifest row.] → No new exposure. Today such rows are already uncommitted until the next checkpoint, and the "Crash mid-checkpoint leaves mirror and manifest consistent" scenario already covers re-detecting them.
- [Sync-over-async blocking in the recorder.] → Confined to one loop on a non-pool thread with no synchronization context. A unit test runs `Execute` from an xUnit test thread to prove it does not deadlock.
- [Default concurrency 1 now uses two threads (a transfer worker and the recorder).] → I/O and SQLite overlap instead of alternating, which should be neutral to positive. Verified against `BackupTransferBenchmarks` before and after.
- [Checkpoints are still only evaluated when a result arrives, so a single very long transfer delays the next checkpoint.] → Unchanged from today. Out of scope.

## Migration Plan

Internal refactor in a single release with no on-disk format, schema, or configuration change. Rollback is a plain revert of the change's commits.
