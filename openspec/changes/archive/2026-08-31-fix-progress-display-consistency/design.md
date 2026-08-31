## Context

`BackupCommand.Create` wires a `Progress<BackupProgress>` whose handler calls
`BackupProgressCalculator.Calculate` and then `Console.Write`s the formatted line
directly (`BackupCommand.cs`). `BackupExecutor.Execute` transfers files with
`Parallel.ForEach` (`MaxDegreeOfParallelism = Environment.ProcessorCount`) and calls
`onBytesTransferred?.Invoke(size)` once per completed file from inside a shared
`reportLock`, so the cumulative `bytesSoFar` value passed into each `progress.Report(...)`
call in `BackupPipeline.Run` is already generated in strictly increasing order at the
source. The problem is entirely on the delivery/render side: `Progress<T>` (no
`SynchronizationContext` present in a console app) posts each `Report()` via
`ThreadPool.QueueUserWorkItem`, which offers no ordering or mutual-exclusion guarantee
across separate posts. A repro confirmed up to 24 concurrent handler executions and
~47% out-of-order deliveries under load. `Console.Write` is internally synchronized
(`SyncTextWriter`), so a single call's text is never character-interleaved with
another's, but a stale, lower cumulative-bytes value can still be fully rendered after a
fresher one already appeared. Separately, the handler renders unconditionally on every
`Report()` call - once per completed file - with no throttling. See proposal.md - Why.

## Goals / Non-Goals

**Goals:**
- Guarantee the rendered progress line never shows a lower cumulative-bytes value than
  one already rendered for the same run.
- Bound how often the line is actually redrawn, independent of file count/frequency.
- Keep the fix scoped to the display layer; no change to manifest format, CLI
  arguments, or run summary output.

**Non-Goals:**
- Making the throttle interval user-configurable (fixed constant is sufficient for this
  fix; can be revisited later if needed).
- Reworking `BackupPipeline`/`BackupExecutor`'s public `IProgress<BackupProgress>`
  contract or their parallelism model.
- General-purpose terminal UI/redraw framework - this only fixes the existing
  single-line progress display.

## Decisions

**Add a stateful "progress display gate" in the CLI/reporting layer, not in the
pipeline.** A new small type (e.g. `ProgressDisplayGate`, alongside
`BackupProgressCalculator` in `Vara.Application.Reporting`) wraps the decision of
whether an incoming `BackupProgress` report should be rendered:
- Tracks the highest `BytesTransferred` value rendered so far; discards (does not
  render) any incoming report whose value is lower than that maximum. Because the
  source-side values are already monotonic per the analysis above, this alone
  eliminates visible regression regardless of the order in which the thread pool
  happens to invoke handlers.
- Tracks the wall-clock time of the last render (via an injectable clock, following
  the same `nowProvider` pattern already used by `BackupProgressCalculator`) and
  suppresses redraws more frequent than a fixed interval (100ms / 10Hz - a common,
  human-readable terminal refresh rate) - **except** it always renders the first
  report of a run (`BytesTransferred == 0`) and the final report
  (`BytesTransferred == TotalBytes`), satisfying the "immediate at start/completion"
  requirement.
- Both checks plus the resulting `Console.Write` are performed inside a single lock
  owned by the gate, so the check-then-render sequence is atomic even though multiple
  `Progress<T>` handler invocations can still run concurrently on the thread pool. This
  lock only guards cheap in-memory comparisons and one `Console.Write` call - never
  disk I/O - so it cannot become a bottleneck for the transfer hot path (which doesn't
  take this lock at all).

*Alternative considered*: replace `IProgress<BackupProgress>` with a plain synchronous
callback (bypassing the `Progress<T>`/thread-pool hop entirely), which would also
remove the reordering possibility at its root. Rejected for this change because it
alters `BackupPipeline`'s public API and touches more call sites/tests than the
observed symptom requires; the display-side gate fully satisfies the new spec
requirements without that broader API change. Left as a possible future simplification
if `Progress<T>`'s indirection proves problematic elsewhere.

*Alternative considered*: install a custom single-thread `SynchronizationContext` before
constructing the `Progress<T>` so callbacks are marshaled onto one dedicated thread in
post order. Rejected as heavier plumbing (a dedicated pump thread/queue) than needed
just to prevent stale-value flicker; the monotonic-value guard achieves the same
user-visible guarantee with a plain lock.

**Throttle interval fixed at 100ms.** Chosen as a standard, widely-used terminal
progress-bar refresh rate - fast enough to feel live, slow enough to stay readable at
a glance. Not exposed as a setting (see Non-Goals).

## Risks / Trade-offs

- [Risk] The monotonic guard could be seen as suppressing legitimate updates → Mitigation: it only discards a render whose byte value is strictly lower than one already shown; equal or higher values always render, so no real progress information is lost, only visual regression is prevented.
- [Risk] Throttling could delay the true final state after a very recent redraw → Mitigation: the final report (`BytesTransferred == TotalBytes`) is always rendered immediately regardless of the throttle window, per spec.
- [Risk] Very short runs might complete before any intermediate redraw fires → Mitigation: start (`BytesTransferred == 0`) and completion are both exempt from throttling, so short runs still show correct initial and final state.
- [Risk] Adding a lock around the render path reintroduces contention → Mitigation: the lock scope is limited to in-memory comparisons and a single `Console.Write`; no source-file I/O happens under it, and it is separate from `BackupExecutor`'s own `reportLock`, so backup transfer throughput is unaffected.
