## Context

`PruneService.Prune(profile)` currently does eligibility evaluation and deletion in one
uninterrupted method: it computes the eligible-for-removal snapshot list via
`RetentionEvaluator.DetermineEligibleForRemoval`, deletes those rows via
`ISnapshotRepository.PruneSnapshots`, then garbage-collects unreferenced content by comparing
`ISnapshotRepository.GetAllReferencedContentHashes()` (which reflects the post-deletion state)
against `IContentStore.ListAllStoredHashes()`. `PruneCommand` calls this once, with no gate.

`RestoreCommand` already implements the pattern this change follows: attempt the operation,
catch `DestinationExistsException`, then branch on `--force` and `Console.IsInputRedirected` to
either prompt (via `StandardError.Console.Confirm`, written to stderr) or fail with a clear error
directing the user to the override flag. See `openspec/specs/snapshot-history/spec.md` -
"Restore a file at a given version or date".

## Goals / Non-Goals

**Goals:**
- Gate `vara prune`'s destructive action behind the same interactive/non-interactive contract
  restore already uses, without changing `PruneService.Prune`'s existing behavior, its run-lock
  semantics, or its return value for callers that already bypass the prompt.
- Make the "how many snapshots" fact available to the CLI layer before deletion happens, since
  (unlike restore's file-exists check) this fact is cheap to compute in advance.

**Non-Goals:**
- Predicting the exact number of blobs that will be garbage-collected before deletion happens.
  That count is only known once `GetAllReferencedContentHashes()` is re-queried after the
  snapshot rows are gone; computing it hypothetically would require a new repository query mode
  purely to enrich a confirmation message, which isn't worth the added surface. The prompt states
  the fact qualitatively ("content no longer referenced will also be freed") instead.
- Closing the (small) race window between preview and confirmation, where a concurrent backup
  run could add a new snapshot while the user is deciding. Restore has an analogous gap between
  its existence check and the user's answer; this change accepts the same class of risk rather
  than restructuring `PruneService` to hold the run lock across a blocking console read.

## Decisions

**Expose eligibility as a public, side-effect-free method on `PruneService`.**
Add `PruneService.CountEligibleForRemoval(Profile profile)` (or equivalent), reusing the existing
`RetentionEvaluator` and `ISnapshotRepository.ListSnapshots()` call already made inside `Prune`.
`PruneCommand` calls this first to decide whether to prompt at all and what count to show, then
calls `Prune(profile)` unchanged if the run proceeds. `Prune` itself is not restructured - it
still throws `RetentionPolicyNotConfiguredException` and `PruneAlreadyRunningException` exactly
as before, so the "no policy configured" and "already running" scenarios in the spec are
unaffected by this change.
- Alternative considered: split `Prune` into `Plan(profile)` / `Execute(plan)`, holding the run
  lock from `Plan` through the CLI's confirmation prompt. Rejected for this change - it changes
  `PruneService`'s existing contract and lock-acquisition timing for a race window judged
  low-probability and low-severity (single-operator CLI tool; worst case is a second run
  rejected with the existing `PruneAlreadyRunningException`, or one extra snapshot pruned in a
  later run - never silent data loss).

**Flag name: `--yes`/`-y`, distinct from restore's `--force`.**
Restore's `--force` means "overwrite without asking" - a statement about *how* to handle a
conflict. Prune's override means "I acknowledge this deletes data, proceed" - a statement about
*authorizing* a destructive action with a magnitude only known at runtime. Using a different name
avoids implying the two flags are interchangeable across commands.

**Confirmation message content: count + qualitative content-freeing note, no blob count.**
Mirrors restore's prompt style (`StandardError.Console.Confirm`, default `false`, written to
stderr so it survives stdout redirection) but adds the one piece of information restore's binary
file-exists check doesn't need: how much is at stake. Full snapshot metadata (timestamps, etc.)
is available from the same preview call if a future iteration wants a richer message, but the
proposal deliberately keeps this one to a count for parity with restore's terseness.

**Skip the prompt entirely when zero snapshots are eligible.**
Mirrors restore's "destination doesn't exist -> write without asking" scenario: a
confirmation only makes sense when there's something to lose. This also means a scheduled/cron
invocation of `vara prune` without `--yes` keeps working unattended on profiles that currently
have nothing to prune, and only starts failing (per the non-interactive rule below) once a
snapshot actually becomes eligible - at which point the operator needs to make a decision anyway.

## Risks / Trade-offs

- [Preview/execute race: a concurrent backup adds a snapshot between the count shown in the
  prompt and the user's confirmation] -> Accepted; matches restore's existing risk profile, and
  the run lock still prevents the actual deletion from overlapping a concurrent backup or prune.
- [A scripted/cron caller that previously ran `vara prune <profile>` unattended now fails once
  that profile has an eligible snapshot, unless updated to pass `--yes`] -> This is the intended,
  restore-consistent behavior change (a destructive default requires an explicit opt-in for
  unattended use), called out in the proposal's "What Changes" and in `tasks.md` as a
  documentation/CHANGELOG item.
