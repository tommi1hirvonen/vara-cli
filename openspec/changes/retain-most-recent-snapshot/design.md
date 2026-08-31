## Context

See proposal.md - Why. Relevant current mechanics:

- `RetentionEvaluator.DetermineRetainedSnapshotIds` builds a retained-id set purely from per-tier bucket math (`RetainNewestPerBucket` for daily/weekly/monthly/yearly). If every configured tier count is 0, the retained set is empty.
- `PruneService.Prune` passes `DetermineEligibleForRemoval`'s result (all non-running snapshots not in the retained set) straight to `SqliteSnapshotRepository.PruneSnapshots`.
- `PruneSnapshots` protects individual `file_versions` rows that are the current (`MAX(id)`), non-deleted row for their path - this is a row-level, not snapshot-level, protection. A snapshot's own row in the `snapshots` table only survives if at least one of its `file_versions` rows remains referenced afterward.
- A completed snapshot that recorded zero file changes (e.g. a no-op re-run) has no `file_versions` rows at all, so it has nothing to anchor it and is deleted whenever it appears in the eligible-for-removal set - even if it is the newest snapshot.

## Goals / Non-Goals

**Goals:**
- Ensure the most recent completed snapshot for a profile is never included in the eligible-for-removal set, independent of tier configuration.
- Keep the fix localized to eligibility determination so existing tiered-retention and garbage-collection behavior is unaffected for every other snapshot.

**Non-Goals:**
- Changing how many older snapshots a tier retains, or the bucket algorithm itself.
- Changing how `file_versions` row-level "current row" protection works in `SqliteSnapshotRepository.PruneSnapshots`.
- Guaranteeing more than one snapshot survives an all-zero policy (only the single newest completed snapshot is guaranteed).
- Special-casing "no file changes" snapshots specifically - the fix is framed generally as "the newest completed snapshot is always retained," which also happens to cover the no-op-run case.

## Decisions

**Where to enforce the guarantee: `RetentionEvaluator`, not `PruneService` or the repository.**
`DetermineRetainedSnapshotIds` already owns "what counts as retained"; adding the newest-snapshot rule there keeps `DetermineEligibleForRemoval` correct by construction and keeps `PruneService`/`SqliteSnapshotRepository` unaware of this special case. Alternative considered: special-case it in `PruneService.Prune` by filtering the newest id out of `eligible` after calling the evaluator. Rejected because it duplicates "what is retained" logic across two places and would silently diverge if `RetentionEvaluator` is ever unit-tested or reused independently of `PruneService`.

**Definition of "most recent": newest by the same `Timestamp` ordering already used for bucketing (`CompletedAt ?? StartedAt`), restricted to `Status == Complete`.**
This matches the existing `completed` list already computed at the top of `DetermineRetainedSnapshotIds`, so the newest entry (if any) is simply `completed.FirstOrDefault()` given the list is already ordered descending by timestamp. No new snapshot-selection logic needed.

**No configuration knob to disable this guarantee.**
The guarantee is a correctness/coherence floor (snapshot history should never appear to fully vanish while retention math is otherwise "working as configured"), not a policy choice. Making it opt-out would reintroduce the exact confusing state this change removes. If a user truly wants zero history, that is already an unsupported/unreachable state today (there is always at least one completed snapshot representing current state as long as any backup has ever run), so no behavior users currently rely on is being removed.

## Risks / Trade-offs

- [A profile with a zero-tier policy and many completed snapshots will now always keep one more snapshot manifest row (and its underlying content) than before.] -> This is the intended fix; disk usage impact is bounded to a single extra snapshot's worth of referenced content, which is already implicitly kept via "current row" protection in the common case (non-empty mirror). Only the true no-op-run / fully-emptied-mirror edge cases change observable prune output.
- [Tests asserting `Zero_count_tiers_contribute_nothing` returns an empty retained set will need updating since the retained set will no longer be empty once at least one completed snapshot exists.] -> Update the existing test fixtures/assertions as part of this change's task list; this is an intentional, expected behavior change captured by the modified spec requirement.
