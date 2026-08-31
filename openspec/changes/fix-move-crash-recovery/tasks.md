## 1. Mirror-side move recovery

- [ ] 1.1 Update `FileSystemContentStore.MoveMirrorEntry` so that when `fromPath` does not exist, it checks whether `toPath` already exists; if so, treat the relocation as already satisfied and return normally instead of throwing `FileNotFoundException`
- [ ] 1.2 Preserve existing failure behavior for the case where neither `fromPath` nor `toPath` exists (genuine failure, unchanged from today) - verify via a new unit test in `FileSystemContentStoreTests.cs` asserting `FileNotFoundException` still propagates
- [ ] 1.3 Add a unit test in `FileSystemContentStoreTests.cs` covering the recovery case: call `MoveMirrorEntry` once (normal relocation), then call it again with the same arguments, and verify the second call returns normally and the destination content is unchanged

## 2. Executor-level regression coverage

- [ ] 2.1 Add a test in `Vara.Application.Tests/Backup` (alongside existing `BackupExecutor` tests) simulating a retried Move operation where the fake content store's `fromPath` is already absent and `toPath` already holds the expected content, verifying `ExecuteMoveOrDelete` completes the move's manifest recording (via `RecordFileVersion` for both `Deleted` and `Moved`) rather than adding the path to `failedPaths`
- [ ] 2.2 Verify the pre-existing "locked mirror entry" failure test(s) for Move still pass unmodified, confirming genuine failures are still reported as failed paths

## 3. Verification

- [ ] 3.1 Run `dotnet test` for `Vara.Infrastructure.Tests` and `Vara.Application.Tests` and verify all tests pass, including the new recovery-path tests
- [ ] 3.2 Manually trace the crash scenario from design.md end-to-end against the updated code (interrupted move -> restart -> next run) to confirm the affected path is recorded as moved and not left in `failedPaths`
