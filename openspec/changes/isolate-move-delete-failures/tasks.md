## 1. Implement fault isolation for Move/Delete

- [x] 1.1 In `BackupExecutor.ExecuteMoveOrDelete`, wrap the Move branch (content-store call plus its `RecordFileVersion` calls) in a try/catch matching `ExecuteTransfer`'s `catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)`, recording `operation.RelativePath` to `failedPaths` and incrementing `counts.Failed` on catch, without calling `RecordFileVersion` for the failed operation. Verify by building the project (`dotnet build`).
- [x] 1.2 Apply the same try/catch to the Delete branch of `ExecuteMoveOrDelete`. Verify by building the project (`dotnet build`).
- [x] 1.3 Thread `failedPaths` and `reportLock` into `ExecuteMoveOrDelete` (passed from `Execute`, same instances used by `ExecuteTransfer`) so failures recorded in the sequential Move/Delete loop and the parallel Add/Change loop accumulate into the same `ExecutionOutcome.FailedPaths` list. Verify by building the project (`dotnet build`).

## 2. Add test coverage

- [x] 2.1 Extend `FakeContentStore` (`tests/Vara.Application.Tests/Backup/Fakes.cs`) with a way to force `MoveMirrorEntry` and/or `RemoveFromMirror` to throw a configured exception (e.g. constructor flags or settable fields), without changing its behavior for existing passing tests.
- [x] 2.2 Add a test asserting that when `MoveMirrorEntry` throws `IOException` (simulating a locked mirror entry), `BackupExecutor.Execute` records the operation's relative path in `outcome.FailedPaths`, increments `FilesFailed`, does not throw, and other operations in the same plan still complete successfully.
- [x] 2.3 Add a test with the same shape as 2.2 but for `RemoveFromMirror` throwing on a Delete operation.
- [x] 2.4 Add a test asserting that when a Move operation fails, no `RecordFileVersion` call was made for that operation's paths (inspect `FakeSnapshotRepository`'s recorded file history for the affected path), while unrelated operations in the same plan are still recorded normally.
- [x] 2.5 Run `dotnet test tests/Vara.Application.Tests` and verify all tests pass, including the new and pre-existing `BackupExecutorTests`.

## 3. Update specs cross-check

- [x] 3.1 Run `openspec validate --change isolate-move-delete-failures --strict` and resolve any reported issues before archiving.
