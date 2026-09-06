## Why

Two independent robustness gaps sit at the tail end of a backup run:

1. `BackupPipeline.Run`'s catch block calls `repository.FailSnapshot(...)` and then
   `manifestBatch.Commit()` before re-`throw`-ing. If `Commit()` itself throws (for example, a
   second, unrelated failure while persisting the failure record), that new exception replaces the
   original one - `throw;` never executes - so the actual root cause of the run's failure is lost in
   favor of a secondary, less informative error.
2. `Progress<T>` (used to drive `BackupCommand`'s live Spectre display) has no `SynchronizationContext`
   to capture in a console app, so it dispatches each report via `ThreadPool.QueueUserWorkItem`
   rather than synchronously. A report - including the run's final one - can therefore still be
   queued and not yet delivered when `pipeline.Run(...)` returns and Spectre's
   `Progress().Start(ctx)` block exits and tears down its rendering context, risking a stray render
   attempt against a torn-down context.

Both are narrow, low-probability races, but both erode the reliability guarantees the
`backup-execution` (error reporting) and `progress-reporting` (display integrity) capabilities are
meant to provide, and both are localized to the same run-completion code path.

## What Changes

- `BackupPipeline.Run`'s catch block preserves the original exception as the one that propagates to
  the caller even if the failure-recording `Commit()` call itself throws (for example, by capturing
  the original exception and combining or prioritizing it, or by isolating the failure-recording
  commit's own exceptions from suppressing the original).
- The backup run's completion path ensures all queued progress reports have been delivered (or are
  no longer able to render) before the live display context is torn down, so a stray render can
  never target a disposed Spectre context.

## Capabilities

### New Capabilities

(none)

### Modified Capabilities

- `backup-execution`: the "Crash and interruption safety" area is extended so that a failure
  encountered while recording a run's own failure state never obscures the original triggering
  exception reported to the caller.
- `progress-reporting`: the "Progress bar reflects run outcome severity" requirement is extended so
  that no progress report can be rendered after the live display has been torn down at the end of a
  run.

## Impact

- **Code**: `src/Vara.Application/Backup/BackupPipeline.cs` (catch block),
  `src/Vara.Cli/Commands/BackupCommand.cs` (`RunWithLiveDisplay`'s `Progress<BackupProgress>` usage
  and `.Start(ctx => ...)` teardown boundary).
- **Tests**: a regression test forcing `manifestBatch.Commit()` to throw inside the catch path and
  asserting the original exception still propagates; a test/repro harness for progress delivery
  completing before teardown (may require an injectable delivery mechanism instead of the default
  thread-pool-backed `Progress<T>`).
- No change to the run's observable success-path behavior or on-disk data.
