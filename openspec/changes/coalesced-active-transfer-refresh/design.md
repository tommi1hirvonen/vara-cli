## Context

See proposal.md for the motivation and scope. The existing pipeline reports one `BackupProgress` stream containing both numeric counters and an active-path snapshot. `ProgressDisplayGate` throttles most reports but admits file-count advances immediately; `BackupCommand` forwards each admitted report to all live progress tasks and calls `ctx.Refresh()`. Progress callbacks can be delivered asynchronously and out of order, so a queued lifecycle snapshot must not be mistaken for current active state.

## Goals / Non-Goals

**Goals:**
- Separate admission of numeric-stat updates from refresh requests caused by active-path changes.
- Let the live display refresh the active list without changing the numeric state already shown.
- Keep lifecycle notifications latest-wins and bounded, and capture the current in-flight set when a refresh is actually serviced.
- Avoid making transfer worker threads wait for terminal rendering.

**Non-Goals:**
- Guarantee that every short-lived transfer is visible.
- Increase the cadence of throughput, ETA, byte, or completed-file statistics.
- Change the plain-output progress mode or add a recently-completed-files display.

## Decisions

### Maintain separate numeric and active-path display state

Keep the latest rate-admitted numeric snapshot distinct from the latest active-path state. An active-path refresh updates only the list rendered below the stats rows; it must not recalculate or replace the byte, file-count, throughput, or ETA values. Numeric reports continue through the existing monotonic guard and 100 ms admission interval, with the initial and final run updates admitted immediately. Intermediate file-count changes no longer bypass the numeric throttle.

**Alternative considered:** Continue passing every combined snapshot through one gate. This couples path visibility to numeric admission and is the root cause of fast transfers disappearing.

### Signal lifecycle changes and capture current state at service time

File-start and file-finish lifecycle notifications request a live refresh, but they do not enqueue immutable path snapshots for later rendering. The refresh coordinator reads the latest active-path state when it services the request. This avoids displaying a completed path from an older queued snapshot when start/finish reports are delivered out of order.

**Alternative considered:** Render the path snapshot attached to each lifecycle event. This can show stale paths when asynchronous delivery lags behind file completion.

### Coalesce requests with a bounded latest-wins dispatcher

Allow at most one active refresh-dispatch operation and one pending indication that state changed while it was being serviced. Repeated lifecycle notifications while a refresh is pending collapse into that pending refresh, which uses the newest state. The dispatcher signals work without waiting for terminal rendering on the transfer thread; any terminal updates remain serialized through the live-display path.

**Alternative considered:** Queue one render callback per event. A burst of tiny transfers could create a backlog whose stale refreshes continue after the corresponding files have finished.

### Keep the active list truthful, with best-effort visibility

The rendered list represents files in flight when the active state is captured for that refresh. Coalescing can collapse both a start and finish for a fast transfer, so that file may never be rendered. Do not retain a completed path merely to make it visible.

**Alternative considered:** Display a recently-transferred path after completion. That changes the meaning of the active list and would require a separate label and lifetime policy.

### Preserve lifecycle cleanup and failure semantics

Both successful and failed transfers continue to remove their paths from the in-flight set through the existing finish lifecycle. Failure cleanup must not increment the successful-file count. Final numeric reporting and display teardown continue to use the existing bounded progress-drain behavior.

## Risks / Trade-offs

- **Very short transfers may remain unseen** -> Accept this to preserve truthful active-only reporting; lifecycle events improve the chance of visibility without promising a display frame per file.
- **Frequent active-path churn can still request frequent redraws** -> Collapse requests while dispatch is pending, keep only one pending refresh, and do not perform terminal rendering synchronously on transfer threads.
- **Numeric completion counts appear at the next admitted stats update** -> Preserve readability by keeping intermediate file-count changes behind the numeric gate; retain immediate initial and final updates.
- **Asynchronous ordering could produce stale state** -> Use a current-state read when servicing a coalesced refresh rather than replaying queued event snapshots.

## Migration Plan

No data migration or dependency change is required. Update the progress reporting contract and implementation together. Rollback consists of restoring the single combined progress gate behavior; no persisted state is affected.
