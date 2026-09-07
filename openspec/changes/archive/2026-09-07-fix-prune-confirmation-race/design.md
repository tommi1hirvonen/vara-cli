## Context
`PruneCommand` calls `PruneService.CountEligibleForRemoval(profile)` (no lock held) to decide
whether to prompt, then, once confirmed, calls `PruneService.Prune(profile, progress)`, which
acquires the run lock and independently recomputes `_evaluator.DetermineEligibleForRemoval(...)`
from the live snapshot list. The `confirm-prune` change's design.md already explicitly accepted
that the *count* shown at the prompt can be stale by the time `Prune` runs, and deliberately chose
not to restructure `PruneService` to hold the run lock across a blocking console read - a decision
this change keeps. What it did not address is that `Prune`'s independent re-evaluation can select
a *different set* of snapshots than the one implied by the count the user confirmed, with the
difference silently rolled into the same removal.

## Goals / Non-Goals

**Goals:**
- Guarantee that whatever `Prune` actually removes is a subset of what was shown to the user at
  the confirmation prompt - never a snapshot that became eligible only afterward.
- Keep the existing "hold the lock only around the actual mutation, not the confirmation prompt"
  structure intact.

**Non-Goals:**
- Closing the count-staleness risk itself (a differently-timed run might show a smaller number
  than would be true a moment later, or the confirmed set might shrink if something else removes
  a snapshot concurrently) - already an accepted, documented trade-off in `confirm-prune`'s
  design.md, and unchanged here.
- Any change to garbage collection: which blobs are unreferenced is still computed from the live
  manifest state *after* snapshot removal completes, exactly as today.
- Changing `--yes`/non-interactive behavior: those paths never showed a confirmed set to the user
  in the first place, so `Prune` continues to evaluate eligibility fresh at execution time for
  them, exactly as today.

## Decisions
- **`CountEligibleForRemoval` returns the actual eligible ids, not just a count.** Add
  `IReadOnlyList<long> ListEligibleForRemoval(Profile profile)` (or change
  `CountEligibleForRemoval`'s return type - see task list) so `PruneCommand` has the concrete set
  it showed the user, not merely its size, to pass through on confirmation.
- **`Prune` accepts an optional `confirmedSnapshotIds` parameter.** When given, `Prune` computes
  the live eligible set as today (needed regardless, since a confirmed id might no longer be
  eligible or might no longer exist) and removes the intersection of the confirmed ids and the
  live eligible set, rather than the full live eligible set. When omitted (`--yes` or the
  zero-eligible fast path), `Prune` behaves exactly as it does today.
- **A confirmed id that disappeared or is no longer eligible is silently skipped, not an error.**
  Removing fewer snapshots than confirmed is always safe; the run's reported `SnapshotsRemoved`
  count simply reflects what was actually removed.
- **A newly-eligible snapshot not in the confirmed set is left alone and picked up by the next
  prune run**, which will show it in that run's own prompt like any other eligible snapshot -
  consistent with "Unreferenced content garbage collection" and "Live mirror unaffected by
  pruning" already treating a partial/incremental prune history as normal.
- **No new locking around the preview call.** `ListEligibleForRemoval` remains lock-free and can
  still be stale in absolute terms (a snapshot in the confirmed set could theoretically already be
  gone by execution time) - handled by the "skip missing ids" decision above, not by acquiring the
  lock earlier.

## Risks / Trade-offs
- [Risk] A user who confirms a count, then a concurrent backup pushes a previously-most-recent
  snapshot into eligibility, sees fewer snapshots pruned this run than intuitively expected from
  a policy standpoint (the newly-eligible one is deferred).
  -> Mitigation: this is strictly safer than the current behavior (which prunes it silently and
  unconfirmed), and the deferred snapshot is simply picked up - visibly, with its own prompt - on
  the next run.
- [Risk] Passing a confirmed id list changes `PruneService.Prune`'s public signature.
  -> Mitigation: additive, optional parameter (default `null` = today's behavior), so any other
  caller of `Prune` (tests, future callers) is unaffected unless it opts in.
