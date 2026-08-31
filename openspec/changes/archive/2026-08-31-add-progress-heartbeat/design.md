## Context

`BackupCommand` wires progress purely reactively today: `IProgress<BackupProgress>.Report` is invoked from `TeeStream`'s chunk-read callback deep inside `BackupExecutor`, flows through `ProgressDisplayGate` (rate-limits redraws to a fixed interval, guards against regressing to a stale value), and `BackupProgressCalculator` (computes percent/throughput/ETA from the current wall-clock time against the run's start time). See proposal.md - Why for why a stalled transfer - one that produces zero chunk-read events for an extended period - leaves all of this frozen: nothing re-invokes `Calculate` if nothing reports new progress.

Two existing behaviors make the fix unusually cheap:
- `BackupProgressCalculator.Calculate` already reads wall-clock time fresh on every call, so simply calling it again later, with the *same* cumulative bytes, naturally produces a lower throughput and a longer ETA - exactly the honest "this is taking longer than expected" signal a stall should show.
- `ProgressDisplayGate.Report`'s regression guard is `BytesTransferred < _maxBytesRendered` (strictly less-than) - a repeated, unchanged byte value is not rejected by it. Combined with its own interval-elapsed check, re-presenting the same `BackupProgress` value after enough wall-clock time has passed is already admitted and rendered by the existing gate, unmodified.

## Goals / Non-Goals

**Goals:**
- Keep the displayed throughput and ETA reacting to elapsed wall-clock time even when the underlying transfer produces no new byte-progress events for a while.
- Reuse `ProgressDisplayGate` and `BackupProgressCalculator` exactly as they are - the gap is a missing periodic trigger, not a flaw in either component's own logic.
- Keep the heartbeat's tick-vs-real-timer mechanism testable without relying on real wall-clock waits in tests.

**Non-Goals:**
- Detecting or reporting *why* a transfer stalled (lock contention, network hiccup, etc.) - out of scope; this only keeps the existing metrics honest during any stall, regardless of cause.
- Changing the chunk-level progress-generation path from the stream-large-file-transfer-progress change, or the redraw rate-limit / regression-guard logic in `ProgressDisplayGate` - both are reused unmodified.
- A "stalled" indicator or warning message - the proposal only asks for throughput/ETA to keep reflecting reality, not for a new UI element flagging a stall explicitly.

## Decisions

### A small `ProgressHeartbeat` component, separate from the real timer
Introduce `Vara.Application.Reporting.ProgressHeartbeat`: it remembers the most recently observed `BackupProgress` (via an `Update` method called from the same place `BackupCommand` already forwards reports to `ProgressDisplayGate`), and exposes a `Tick` method that re-presents that last-known value to a caller-supplied render action - a no-op until at least one `Update` has happened. `BackupCommand` wires a real `System.Threading.Timer` to call `Tick` on a fixed period, disposed (stopping the timer) once `BackupPipeline.Run` returns.

Separating `Tick` (the re-presentation logic) from the real timer that drives it means a test can call `Tick` directly and assert on the resulting render, without waiting on real wall-clock time or a real background timer - consistent with this codebase's existing pattern of injecting a `nowProvider`/similar seam for deterministic time-dependent tests (e.g. `ProgressDisplayGate`'s and `BackupProgressCalculator`'s own `nowProvider` parameters).

Alternative considered: drive the heartbeat from inside `BackupPipeline`/`BackupExecutor` itself (e.g. a periodic check between operations). Rejected: the whole point is to keep displaying progress *during* a single stalled read that the pipeline's own call stack is blocked inside of - only a genuinely separate thread (a real timer) can tick while that stack is blocked.

### Heartbeat re-presents through the existing gate, not around it
`ProgressHeartbeat.Tick`'s render callback is the same `displayGate.Report(progress, render)` call `BackupCommand` already uses for real reports - not a separate rendering path. This is what lets a repeated byte value still pass the gate's regression guard and (once the gate's own interval has elapsed) get admitted, invoking `BackupProgressCalculator.Calculate` again with fresh elapsed time. No changes to either component are needed.

### Heartbeat interval
A fixed interval, comfortably larger than `ProgressDisplayGate`'s own 100ms redraw interval, so a heartbeat tick reliably passes the gate's interval check when due rather than frequently coinciding with an already-rate-limited window. The exact value (a few hundred milliseconds) is an implementation detail to tune during implementation - the spec only requires it be longer than the display's normal redraw interval, not a specific number.

## Risks / Trade-offs

- [A heartbeat tick could race with the pipeline's own final 100%-complete report, or with `ProgressHeartbeat`/timer disposal right as the run finishes] → Harmless: `ProgressDisplayGate`'s regression guard and interval check make an extra or slightly-late redraw idempotent in effect; at worst one redundant line is written, never a stale or lower value shown after a higher one.
- [A background `System.Threading.Timer` now runs for the duration of every backup run, however short] → Negligible overhead (single lightweight timer, disposed promptly at run end); no behavior change for runs that complete quickly, since `Tick` is a no-op until `Update` has been called at least once and the gate still governs actual rendering.
- [If a run's transfer stage never stalls, the heartbeat never has visible effect beyond what already happens today] → Expected; this change only fills the gap that appears during a genuine stall, not a general behavior change for the common case.
