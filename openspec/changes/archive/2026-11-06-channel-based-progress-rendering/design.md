## Context

See proposal.md - Why. Relevant current state:

- `BackupPipeline.Run` exposes two delivery paths to the CLI: an `IProgress<BackupProgress>` that receives byte/file progress from worker threads (per-chunk callbacks computed with `Interlocked`) and the calling thread, and an `Action<Func<IReadOnlyList<string>>>` active-path refresh request raised by workers on every file start/finish. Neither contract changes.
- `ProgressDisplayGate` implements the "rate-limited", "first/final always rendered", and "never regresses" rules from the `progress-reporting` spec. It is also used by the restore display, which reports from a single thread. It stays as-is.
- `progress-reporting` requires that no report renders after the live display is torn down, even when delivery is asynchronous ("Progress delivery completes before display teardown"), and that the outcome color is stamped on the bar ("Progress bar reflects run outcome severity"). Today a 2-second bound keeps a stuck callback from hanging exit; after the bound, `displayOpen = false` under `displaySync` guarantees that no late render touches the context.
- An exception thrown by a `TrackedProgress` handler escapes a `ThreadPool.QueueUserWorkItem` callback and would terminate the process; an exception in the active-path refresh is captured and rethrown after the run by `ThrowIfFaulted`.

## Goals / Non-Goals

**Goals:**
- Exactly one consumer invokes the backup display's render callbacks; producers only enqueue, never block, and never touch Spectre.
- A single bounded close step that preserves the delivery-before-teardown guarantee and the outcome stamping on both the normal and the exception path.
- The render loop is testable without Spectre, the same way the dispatcher and `TrackedProgress` are today (plain callbacks in, observable calls out).

**Non-Goals:**
- Changing `BackupPipeline`, `BackupExecutor`, `ProgressDisplayGate`, `BackupProgressCalculator`, or `BackupProgressColumn` behavior.
- Making the pipeline asynchronous. The command stays synchronous; only the consumer runs as a background task.
- Guaranteeing global ordering of progress reports across producers (see Risks).

## Decisions

### 1. One unbounded, single-reader channel of display events

A new `internal sealed class BackupProgressRenderLoop` in `Vara.Cli/Presentation` owns a `Channel<DisplayEvent>` created with `Channel.CreateUnbounded(new UnboundedChannelOptions { SingleReader = true, SingleWriter = false })`. `DisplayEvent` is a small closed hierarchy (or union) with two cases: `ProgressReported(BackupProgress)` and `ActivePathsRequested(Func<IReadOnlyList<string>> capture)`.

The loop implements `IProgress<BackupProgress>` (`Report` → `TryWrite(ProgressReported)`) and exposes `RequestActivePathsRefresh(Func<IReadOnlyList<string>>)` (→ `TryWrite(ActivePathsRequested)`), so it plugs into `pipeline.Run(profile, loop, loop.RequestActivePathsRefresh, ...)` without any pipeline change.

**Why unbounded:** producers are transfer workers. A bounded channel would need either a blocking write, which stalls I/O behind rendering, or a drop policy, which could drop the first or final report that the gate must always render. Today's thread-pool queue is equally unbounded, and the consumer is cheap because the gate discards most reports immediately, so the backlog stays small in practice.

**Alternatives considered:**
- `BlockingCollection<T>`: synchronous and older, with no `Completion` task to drain against.
- Keeping `TrackedProgress` and replacing only the dispatcher: leaves the per-report thread-pool fan-out and the second drain in place.
- Two channels (progress and active paths): adds a merge point and two drains for no benefit.

### 2. Every progress report passes through the gate; only active-path requests are coalesced

The consumer drains whatever is currently buffered as one batch. In that batch it:
- passes every `ProgressReported` event to `ProgressDisplayGate.Report(progress, render)` in channel order;
- keeps only the latest `ActivePathsRequested` capture, then evaluates it once at the end of the batch and invokes the active-path refresh callback with the result.

Progress reports are deliberately not collapsed to "latest only". The gate's "first report always renders" rule seeds `BackupProgressCalculator`'s throughput clock, and collapsing could skip that report and skew throughput/ETA. Coalescing active-path requests per batch preserves the spec's "no unbounded backlog" and "latest available in-flight state at capture time" rules. Evaluating the capture on the consumer, as the dispatcher does today, preserves "a refresh does not display a path known to have completed before capture".

**Alternative considered:** a `DropOldest` channel of capacity 1 for active-path requests. It is equivalent in effect but needs a second channel (see Decision 1).

### 3. The consumer is a background task; the command thread stays synchronous

`pipeline.Run` executes synchronously inside Spectre's `Progress().Start(ctx => ...)` body, so the consumer must run elsewhere. It is started with `Task.Run` as an `async` loop over `reader.WaitToReadAsync()` / `TryRead`. Render callbacks are synchronous. This matches today's threading, where renders already run on thread-pool threads, so Spectre sees no new access pattern.

### 4. Close is the only teardown entry point and guarantees "no callback afterwards"

`bool Close(TimeSpan drainTimeout, Action? finalRender = null)`:

1. completes the writer, so later `Report`/refresh calls are ignored (`TryWrite` returns false);
2. waits for the consumer to finish, up to `drainTimeout`;
3. under the loop's single `Lock`, sets `closed = true` and runs `finalRender` (the outcome stamping);
4. returns whether the drain finished within the bound.

The consumer takes the same `Lock` around each callback invocation and skips callbacks once `closed` is set. In the normal case the lock is uncontended, because only the consumer uses it until Close. It exists so that after a drain timeout, a consumer still stuck in or about to enter a callback cannot render after `finalRender` or after Spectre tears down the context. This replaces both `displaySync`/`displayOpen` in `BackupCommand` and the two drains, while keeping today's 2-second bound and best-effort semantics.

**Alternative considered:** sending a `Close(outcome)` event through the channel and letting the consumer stamp the outcome. Rejected because on a drain timeout the outcome would never be stamped, which would regress "Progress bar reflects run outcome severity" in the timeout path.

### 5. Faults are captured, stop intake, and are rethrown after the run

If a render callback throws, the consumer completes the channel writer with that exception (`TryComplete(ex)`), so producers stop enqueuing and no backlog accumulates, and exits. `ThrowIfFaulted()` rethrows the captured exception, preserving its stack via `ExceptionDispatchInfo`. `RunWithLiveDisplay` calls it after `Close` on the success path, exactly where it calls the dispatcher's `ThrowIfFaulted` today. This extends today's active-path fault behavior to progress renders, replacing the process-terminating thread-pool exception. That is strictly more graceful and changes no specified behavior.

### 6. The plain output path uses the same loop without active paths

`RunWithPlainOutput` constructs the loop with only a render callback and passes it to `pipeline.Run` instead of `new Progress<BackupProgress>(...)`, then calls `Close(drainTimeout)` before returning. This removes per-report thread-pool fan-out and ensures no progress line is printed after the method returns, so none can interleave with the run summary.

## Risks / Trade-offs

- [**Progress reports from different workers can still arrive out of order.**] Each worker computes `Interlocked.Add(ref bytesSoFar, ...)` and then writes, so two workers can enqueue in the opposite order of their totals. → The channel only removes the extra reordering from thread-pool dispatch. `ProgressDisplayGate`'s never-regress guard remains required and stays in place; its doc comment is updated to describe the real source of reordering.
- [Unbounded channel growth if the consumer stalls.] → Same exposure as today's thread-pool queue. The gate keeps per-event work trivial, and a faulted consumer completes the writer so producers stop enqueuing.
- [`Close` timing out leaves the consumer task running briefly in the background.] → The `closed` flag under the lock guarantees it invokes no further callbacks. This matches today's best-effort drain contract.
- [Behavior shift: a progress render exception now surfaces as a command error after the run instead of terminating the process.] → It surfaces through the same path the active-path refresh already uses; no spec covers the crash behavior.

## Migration Plan

Internal refactor in a single release with no data or configuration impact. Rollback is a plain revert of the change's commits.
