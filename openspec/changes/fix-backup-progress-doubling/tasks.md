## 1. Pipeline and Executor Progress Scaling

- [ ] 1.1 In `BackupPipeline.Run`, set `progressTotalBytes = plan.TotalBytesToTransfer` unconditionally (removing the `* 2` multiplier for non-hardlink targets). Verify by inspecting `BackupPipeline.cs`.
- [ ] 1.2 In `BackupExecutor.ExecuteTransfer`, implement proportional progress reporting when `contentStore.SupportsHardlinks` is false: apportion 50% of the file size to `StoreFromStream` and 50% to `PlaceAtMirrorPath`, reconciling any remainder at completion so the cumulative bytes reported for the file equals its exact size. When `contentStore.SupportsHardlinks` is true, pass progress to `StoreFromStream` and suppress progress in `PlaceAtMirrorPath`.
- [ ] 1.3 Add unit tests in `BackupExecutorTests` verifying that on a non-hardlink content store, chunk progress fires across both `StoreFromStream` and `PlaceAtMirrorPath` and sums to the exact file size (including for odd-length files), and on a hardlink-capable store, hardlink fallback does not double-count.

## 2. Pipeline Integration Tests and Verification

- [ ] 2.1 Update `BackupPipelineTests.Progress_never_exceeds_100_percent_when_the_target_has_no_hardlink_support` to verify that `r.TotalBytes == plan.TotalBytesToTransfer`, max reported `r.BytesTransferred == plan.TotalBytesToTransfer`, and progress does not exceed 100%.
- [ ] 2.2 Add or update tests verifying that dry run (`PlanOnly`), live progress (`BackupProgress`), and final summary (`SnapshotStats.BytesTransferred`) all agree on the exact same total bytes on targets without hardlink support.
- [ ] 2.3 Run full solution test suite (`dotnet test`) and verify all tests pass.
