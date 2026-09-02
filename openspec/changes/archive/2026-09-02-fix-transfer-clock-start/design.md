## Context

See proposal.md - Why. Today, `BackupPipeline.Run` emits the first `BackupProgress`
report (`progress.Report(new BackupProgress(0, progressTotalBytes))`) immediately
after planning, before calling `BackupExecutor.Execute`. `Execute` then runs its
sequential Move/Delete pass first, and only afterwards starts the parallel Add/Change
transfer pass that actually calls `onBytesTransferred`. On the interactive path,
that first report is also what makes `BackupCommand`'s `Render` swap the scan
spinner for the bar/stats tasks, which in turn is what triggers
`BackupProgressCalculator`'s first `Calculate()` call and thus its lazy
`_startedAt ??= now`. So the clock starts at "planning just finished", not at "byte
transfer just started", with the whole Move/Delete pass silently ticking on the
clock in between.

## Goals / Non-Goals

**Goals:**
- Make the calculator's elapsed-time basis start only once byte transfer begins,
  excluding the Move/Delete pass's duration entirely.
- Keep the fix minimal and confined to the reporting/signaling boundary; no change
  to `BackupProgressCalculator`'s own averaging logic.

**Non-Goals:**
- Not revisiting the lifetime-cumulative-average vs. windowed/EMA throughput
  question - separate, deliberately deferred topic.
- Not adding file-count-based weighting to the ETA estimator (small-file overhead
  distorting mid-run throughput) - a known, accepted limitation, not addressed here.
- Not changing run summary statistics or the byte-based percent-complete formula.

## Decisions

**Move the first `progress.Report` call to fire only when byte transfer is about to
start, via a new one-shot callback from `BackupExecutor.Execute`.**

Add an optional `Action? onTransferPhaseStarting` parameter to
`BackupExecutor.Execute`, invoked exactly once, unconditionally, right after the
sequential Move/Delete loop completes and right before the parallel Add/Change loop
begins (even when one or both operation lists are empty, so a plan with nothing to
transfer still reports its 0/0 completion correctly). `BackupPipeline.Run` supplies
this callback to perform the `progress?.Report(new BackupProgress(0,
progressTotalBytes))` call that today happens before `Execute` is invoked at all,
and stops making that call up-front.

`BackupProgressCalculator` itself needs no code change: it already lazily sets
`_startedAt` on its own first `Calculate()` call. Moving *when* the first
`BackupProgress` report is emitted is sufficient to move *when* that first
`Calculate()` call happens, since the interactive path's bar/stats tasks (which
drive `Calculate()` via Spectre's `AutoRefresh`) and the plain-output path's own
`Calculate()` call are both only ever triggered by an admitted `BackupProgress`
report.

Alternatives considered:
- *Split `Execute` into `ExecuteMoveDeletes` + `ExecuteTransfers`, called
  separately by `BackupPipeline`.* Rejected: a larger, riskier diff (splits
  `Counts`/`failedPaths` state across two calls) for no benefit over a callback -
  `Execute` already exposes an analogous callback (`onBytesTransferred`) for this
  same kind of interleaved signaling.
- *Add an explicit `BackupProgressCalculator.Start()`/`Reset()` method called
  directly by `BackupCommand`.* Rejected: `BackupCommand` has no visibility into
  "Move/Delete just finished" on its own - it would still need the same new hook
  from `BackupExecutor`, just routed to a second method on the calculator instead of
  through the existing progress-report stream, adding a second synchronization path
  for no benefit.

**Side effect: the scan-phase spinner now spans the Move/Delete pass too.**

Since the interactive UI swaps from the spinner to the bar/stats display on the
first admitted report, and that report now fires later (after Move/Delete instead
of before it), the spinner will visibly persist a bit longer for runs with a
non-trivial Move/Delete batch. This is an accepted, minor UX side effect of the fix,
not a regression: showing "Scanning..." through a phase that transfers no bytes is
at least as accurate as showing a static 0.0% bar with no ETA, which is what
appears today for the same duration. The non-interactive/plain-output path
(`RunWithPlainOutput`) renders nothing until a report is admitted either way, so it
is unaffected in shape - only its first line's timing shifts along with the report.

## Risks / Trade-offs

- [Risk] A Move/Delete operation throws mid-pass → today, per-operation failures
  are caught and recorded without aborting the run (backup-execution spec:
  "Unreadable files do not abort the run" applies equally to Move/Delete's own
  try/catch in `ExecuteMoveOrDelete`), so the loop still completes and the new hook
  still fires normally afterward. → Mitigation: none needed; existing failure
  handling is unchanged and already covers this.
- [Risk] A plan with empty Move/Delete and empty Add/Change lists (nothing to do)
  must still report its (likely 0/0) completion. → Mitigation: the hook is invoked
  unconditionally, regardless of whether either loop had any operations to process.
- [Risk] Any existing test asserting the transfer UI/first report appears
  immediately when `Execute` is entered (before Move/Delete runs) would need
  updating to reflect the new, later timing. → Mitigation: locate and update such
  assertions during implementation; this is expected, in-scope test churn, not a
  design risk.

## Migration Plan

Not applicable - no persisted data, stored schema, or external API is affected.
This is a behavior-only change confined to a single backup run's in-memory
progress-reporting timing; no rollout or rollback sequencing is needed beyond the
normal release process.
