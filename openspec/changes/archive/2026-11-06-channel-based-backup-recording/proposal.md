## Why

`BackupExecutor` runs Add/Change transfers on `Parallel.ForEach` workers that each take a shared `reportLock` to write the file's manifest row, update the run counters, append to `failedPaths`, and decide whether a checkpoint commit is due. The single SQLite connection is therefore touched from whichever worker currently holds the lock, and four kinds of shared mutable state (manifest batch, counters, failure list, checkpoint clock) depend on every call site remembering to take the same lock. Moving the per-file outcome into a message - workers produce results, one recorder consumes them - gives that state a single owner, removes the lock entirely, and confines all SQLite access to the thread that already begins and completes the snapshot. It also turns a later concurrency redesign, such as a possible G# migration of `Vara.Application`, into a translation rather than a redesign.

## What Changes

- Transfer workers no longer touch the repository, manifest batch, counters, or failure list. Each finished Add/Change operation produces an immutable per-file result (transferred, with its recorded metadata, or failed) that is written to a `System.Threading.Channels` channel.
- A single recorder running on the thread that called `Execute` drains that channel. It alone records manifest rows, updates counters and `failedPaths`, runs the periodic checkpoint, and raises `onFileTransferred`.
- The sequential Move/Delete/Link pass already runs on the calling thread before transfers start; it updates the same recorder-owned state directly, without a lock.
- `reportLock` and the lock-guarded `Counts` access are removed; `Counts` becomes plain single-owner state.
- Cooperative Ctrl+C is unchanged in behavior: workers still stop starting new operations, in-flight transfers finish, and every result they produce is recorded before `Execute` returns, so the pipeline's forced checkpoint still covers all completed work.
- Per-file I/O failures, including torn reads detected after transfer, remain results rather than exceptions, so one bad file never aborts the run. An unexpected worker exception still aborts the run after the results already produced are recorded. A recorder failure stops the workers and is surfaced to the pipeline's existing failure handling.
- `BackupExecutor.Execute`'s signature, `ExecutionOutcome`, and `BackupPipeline` are unchanged.

## Capabilities

### New Capabilities
<!-- None: pure internal refactoring of how BackupExecutor serializes per-file outcomes. -->

### Modified Capabilities
<!-- None: backup-execution requirements (unreadable files do not abort the run, torn-read detection, periodic checkpoints, crash/interruption safety, graceful Ctrl+C, configurable transfer concurrency) and progress-reporting requirements are preserved unchanged. skip_specs: true is set. -->

## Impact

- **Code**: `src/Vara.Application/Backup/BackupExecutor.cs`. No other production file changes.
- **Tests**: Existing `BackupExecutorTests`, `BackupPipelineTests`, and the real-repository integration tests (`BackupExecutorCheckpointRealRepositoryTests`, `BackupPipelineRealRepositoryTests`) must pass unchanged. New tests cover single-thread repository access, recording of every result before return on graceful cancellation, and the failure paths for workers and the recorder.
- **Performance**: With the default `transfer_concurrency` of 1, transfer I/O and manifest writes now overlap on two threads instead of alternating on one. Verified against `BackupTransferBenchmarks`.
- **Contracts and dependencies**: No public API change. `System.Threading.Channels` is part of the shared framework, so no new packages are needed, and it is Native AOT compatible. Independent of `channel-based-progress-rendering`; recommended to land after it.
