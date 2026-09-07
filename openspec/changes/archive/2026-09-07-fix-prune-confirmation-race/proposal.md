## Why
`vara prune` decides whether to show a confirmation prompt using
`PruneService.CountEligibleForRemoval`, called before the run lock is acquired. `Prune` itself,
once the user confirms and the lock is held, re-evaluates eligibility from scratch. The
`confirm-prune` change that introduced this prompt already explicitly accepted that the *count*
shown in the prompt can be stale by the time pruning actually runs - deliberately choosing not to
hold the run lock across a blocking console read. What it did not close is a narrower, avoidable
gap: today, `Prune` can end up removing a *different* set of snapshots than the one the user was
told about and confirmed, with no further disclosure. A concurrent backup completing between the
prompt and the confirmation can make an additional snapshot eligible (for example, by displacing
which snapshot counts as "the most recent"), and that snapshot is silently pruned alongside the
ones the user actually saw.

## What Changes
- Capture the specific set of eligible snapshot ids at preview time (not just their count), and
  pass that confirmed set through to `Prune` when the user proceeds.
- `Prune` removes exactly the confirmed snapshot ids that are still present and still eligible at
  execution time - never a snapshot that became eligible only after the user was prompted. Such a
  snapshot is simply deferred to the next prune run, where it will be shown and confirmed like any
  other.
- When no confirmation was required (zero eligible at preview time, or `--yes` given), behavior is
  unchanged: `Prune` still evaluates eligibility fresh at execution time, since there was no
  specific confirmed set to honor.
- This does not reintroduce holding the run lock across the confirmation prompt, and does not
  change the already-accepted risk that the *count* shown can be stale - it only guarantees that
  whatever is actually removed was part of what the user confirmed.

## Capabilities

### Modified Capabilities
- `retention-pruning`: the prune command's confirmation requirement is tightened so that a prune
  run never removes a snapshot the user was not shown and did not confirm.

## Impact
- `Vara.Application.Retention.PruneService` (`CountEligibleForRemoval`, `Prune`).
- `Vara.Cli.Commands.PruneCommand` (passes the confirmed snapshot ids captured at preview time
  into `Prune` instead of only a count).
- No behavior change for the common case (nothing newly eligible between preview and execution).
