## 1. Expose the confirmed snapshot id set

- [ ] 1.1 Add `PruneService.ListEligibleForRemoval(Profile profile)` returning
      `IReadOnlyList<long>` (the snapshot ids `DetermineEligibleForRemoval` selects), and update or
      retire `CountEligibleForRemoval` in favor of it (e.g. keep `CountEligibleForRemoval` as a
      thin wrapper calling `.Count` on the new method, to avoid churning any other caller)
      and verify with a unit test that it returns the same ids `Prune` would otherwise select
- [ ] 1.2 Update `PruneCommand` to call `ListEligibleForRemoval`, use its count for the prompt
      exactly as today, and retain the returned id list for use after confirmation

## 2. Honor the confirmed set in `Prune`

- [ ] 2.1 Add an optional `confirmedSnapshotIds: IReadOnlyList<long>? = null` parameter to
      `PruneService.Prune`
- [ ] 2.2 When `confirmedSnapshotIds` is given, compute the live eligible set as today, then prune
      only the intersection of `confirmedSnapshotIds` and the live eligible set; when omitted,
      prune the full live eligible set exactly as today
- [ ] 2.3 Update `PruneCommand` to pass the id list captured in 1.2 into `Prune` on the confirmed
      and `RunPrune`-without-prompt (zero-eligible) paths where a confirmed set exists; pass
      nothing (`--yes` / no prior preview) where it doesn't

## 3. Tests

- [ ] 3.1 Add a `PruneService` test: call with a `confirmedSnapshotIds` list, then simulate an
      additional snapshot becoming eligible before `Prune` runs (e.g. insert a new snapshot between
      computing the confirmed set and calling `Prune`), and verify only the confirmed ids are
      removed and the new one survives
- [ ] 3.2 Add a test where one confirmed id no longer exists at execution time, and verify `Prune`
      removes the remaining confirmed ids without throwing
- [ ] 3.3 Add a regression test confirming `Prune` called without `confirmedSnapshotIds` (the
      `--yes` path) still prunes the full live eligible set exactly as before
- [ ] 3.4 Update/add a `PruneCommand` test verifying the confirmed id list captured at the prompt
      is what gets passed to `Prune`
- [ ] 3.5 Run the full test suite and verify all tests pass

## 4. Verification

- [ ] 4.1 Manually run `vara prune` interactively against a profile with eligible snapshots, and
      confirm the prompt's count and the actually-removed snapshots match in the normal case
