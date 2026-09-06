## Context

Two independent tail-of-run robustness gaps share the same code path (`BackupPipeline.Run` and
`BackupCommand.RunWithLiveDisplay`), which is why they're bundled into one change. See proposal.md
for the user-facing motivation for each.

For the exception issue: `BackupPipeline.Run`'s `catch` block calls `repository.FailSnapshot(...)`
then `manifestBatch.Commit()` then `throw;` - if `Commit()` throws, `throw;` never executes and the
`Commit()` exception silently becomes the one seen by the caller instead.

For the progress issue: `BackupCommand` constructs `new Progress<BackupProgress>(p =>
displayGate.Report(p, Render))` and passes it into `pipeline.Run(...)`, all inside a synchronous
Spectre `Progress().Start(ctx => { ... })` block. `Progress<T>` has no `SynchronizationContext` to
capture in a console app, so `Report()` calls are always dispatched via
`ThreadPool.QueueUserWorkItem`, asynchronously relative to the call site - including calls made from
deep inside `BackupExecutor`'s per-file loop, which is the last thing to run before `pipeline.Run`
returns.

## Goals / Non-Goals

**Goals:**
- The original exception from a failed run is always what the caller (and thus
  `BackupCommand`/`ErrorReporting`) ultimately sees, even if failure-recording itself throws.
- No progress render call can execute against a Spectre context that `Progress().Start` has already
  torn down.

**Non-Goals:**
- Redesigning `BackupPipeline`'s overall error-handling strategy or exception types - this only
  fixes the specific case of losing the original exception to a secondary one.
- Replacing `Progress<T>` with a different progress-reporting primitive across the codebase - this
  fix only needs to guarantee ordering relative to teardown, not change the reporting mechanism
  everywhere it's used (e.g. `PruneCommand`'s own `Progress<PruneProgress>` usages are a separate,
  unaffected concern unless a shared root cause emerges during implementation).

## Decisions

- **Wrap the failure-recording block in its own `try`/`catch`, log/discard a secondary exception
  from `Commit()`, and always end with `throw;` (or `ExceptionDispatchInfo.Capture(originalEx)
  .Throw()`)** so the original exception is what's guaranteed to propagate.
  - Alternative considered: wrap both in an `AggregateException` when both fail. Rejected -
    `ErrorReporting`'s existing top-level catch is written around single, specific exception types;
    an `AggregateException` would need unwrapping everywhere and provides little value for a
    single-threaded CLI failure path.
- **Introduce an explicit "drain" step after `pipeline.Run` returns (success or throw) and before
  the `Progress().Start(ctx => ...)` lambda exits**, that blocks until all `Progress<T>.Report`
  callbacks queued so far have executed - for example, by having `BackupPipeline`/`BackupExecutor`
  report progress through a small wrapper that also exposes a way to await "everything queued has
  been delivered" (e.g. tracking outstanding `QueueUserWorkItem` completions), rather than relying
  on `Progress<T>` alone.
  - Alternative considered: switch `BackupCommand` to run `pipeline.Run` and progress delivery on a
    captured `SynchronizationContext` (e.g. via `SynchronizationContext.SetSynchronizationContext`)
    so `Progress<T>` delivers synchronously on the calling thread. Rejected as a larger behavioral
    change to how progress is threaded through the whole run, with its own risk of deadlocking the
    UI thread if a report ever blocks; a bounded drain/wait is a smaller, more targeted fix.
- **The drain step has a bounded timeout** (not an unbounded wait) so a stuck reporting callback
  cannot hang the CLI's exit indefinitely; exceeding it is treated as best-effort (proceed with
  teardown anyway) rather than a hard failure, since worst case is the pre-existing rare visual
  glitch, not data loss.

## Risks / Trade-offs

- [Draining outstanding progress callbacks adds a small amount of latency at the end of every
  backup run] → Mitigated by the drain only needing to wait for reports already queued at the
  moment `pipeline.Run` returns, which in practice is at most the handful of reports racing the
  final one, not a meaningful delay.
- [Swallowing the secondary `Commit()` exception in the catch block could hide a real, separate bug
  in failure recording] → Mitigated by still surfacing it (e.g. logged via the same channel already
  used for diagnostics) even though it does not become the thrown exception.

## Open Questions

(none)
