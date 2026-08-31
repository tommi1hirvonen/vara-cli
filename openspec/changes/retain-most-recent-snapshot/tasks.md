## 1. Retention evaluator change

- [x] 1.1 Update `RetentionEvaluator.DetermineRetainedSnapshotIds` in `src/Vara.Application/Retention/RetentionEvaluator.cs` so the newest snapshot in the already-computed `completed` list (ordered descending by `Timestamp`) is always added to the retained set, independent of tier bucket counts. Verify by inspecting the diff: the retained set for a non-empty `completed` list can never end up empty.
- [x] 1.2 Verify `DetermineEligibleForRemoval` requires no changes because it derives from `DetermineRetainedSnapshotIds`, and confirm this with a quick manual trace or a focused unit test.

## 2. Evaluator tests

- [x] 2.1 Update `Zero_count_tiers_contribute_nothing` in `tests/Vara.Application.Tests/Retention/RetentionEvaluatorTests.cs` to reflect that the newest completed snapshot is now retained even when every tier count is 0; verify the test asserts the retained set contains exactly the newest snapshot's id (not empty).
- [x] 2.2 Add a test asserting that with an all-zero-tier policy and multiple completed snapshots, only the single newest completed snapshot appears in the retained set and all others are eligible for removal. Verify it passes.
- [x] 2.3 Add a test asserting the newest-snapshot guarantee holds even for a non-zero tier policy where tier bucket math alone would not have retained the newest snapshot (e.g. bucket counts already exhausted by older same-bucket snapshots). Verify it passes. **Note:** `RetainNewestPerBucket` always processes snapshots newest-first, so any tier with count >= 1 necessarily claims a bucket for the overall newest snapshot - bucket math can only fail to retain the newest snapshot when every tier count is 0. The all-zero-tier scenario is therefore the only case that isolates this guarantee from tier math; implemented as `Newest_completed_snapshot_is_retained_even_when_no_tier_bucket_math_would_have_retained_it`.
- [x] 2.4 Add a test asserting a `Running` or non-`Complete` snapshot that happens to be the most recent overall is not what gets retained by this rule - the guarantee applies to the newest *completed* snapshot only. Verify it passes.

## 3. Repository / prune integration tests

- [x] 3.1 Add a test in `tests/Vara.Infrastructure.Tests/Snapshots/SqliteSnapshotRepositoryTests.cs` (or a `PruneService` integration test if one exists) reproducing the no-op-run scenario: a completed snapshot with zero `file_versions` rows is the newest snapshot, an all-zero-tier policy is applied, and `PruneSnapshots`/`PruneService.Prune` is invoked with the evaluator's eligible set. Verify the newest snapshot's row still exists in `ListSnapshots()` afterward. **Implemented as** `Prune_never_removes_the_most_recent_completed_snapshot_even_with_an_all_zero_tier_policy` in `tests/Vara.Application.Tests/Retention/PruneServiceTests.cs`, since a `PruneService` integration test already existed there and exercises the full evaluator + repository path together.
- [x] 3.2 Verify existing tests `PruneSnapshots_preserves_the_current_row_for_a_still_live_path_even_if_its_snapshot_is_pruned` and `PruneSnapshots_removes_a_snapshot_entirely_once_none_of_its_rows_are_current` still pass unmodified (they exercise older, non-newest snapshots, so behavior for those is unchanged).

## 4. Full verification

- [x] 4.1 Run the full test suite (`dotnet test`) and verify all tests pass, including the updated and new retention/prune tests. **Result:** 66 + 53 + 28 = 147 tests passed, 0 failed, across all three test projects.
