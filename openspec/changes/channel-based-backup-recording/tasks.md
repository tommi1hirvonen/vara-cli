## 1. Results Channel and Recorder

- [ ] 1.1 In `src/Vara.Application/Backup/BackupExecutor.cs`, add a private closed `TransferResult` hierarchy (`Transferred`, `Failed`) and extract the recorder-side handling: record, counters, `failedPaths`, `CheckpointIfDue`, then `onFileTransferred` (design.md Decisions 2 and 3). Verify that `dotnet build Vara.slnx` succeeds.
- [ ] 1.2 Change `ExecuteTransfer` so workers publish a `TransferResult` instead of taking `reportLock`. Call `onFileFinished` in `finally` before publishing, and publish `Failed` for torn reads and for `IOException`/`UnauthorizedAccessException` (Decisions 4 and 5). Verify the lifecycle-callback tests in `BackupExecutorTests` pass unchanged.
- [ ] 1.3 Restructure `Execute`: run the Move/Delete/Link pass on the calling thread without a lock; run `Parallel.ForEach` inside `Task.Run`, completing the writer on success or with the exception on failure; run the recorder loop on the calling thread until the channel completes; then remove `reportLock` and the `Lock` parameters (Decisions 1 and 3). Verify that `dotnet test tests/Vara.Application.Tests --filter "FullyQualifiedName~BackupExecutor"` passes unchanged.

## 2. Failure and Cancellation Semantics

- [ ] 2.1 Implement the unexpected-worker-exception path: record the results already published, then rethrow the transfer task's original exception (Decision 5). Verify with a new test that one worker throws a non-I/O exception while others succeed, that the successful files are recorded, and that `Execute` throws the same exception type as before.
- [ ] 2.2 Implement the recorder-failure path: cancel the private abort token, wait for the transfer task, then rethrow the recorder's exception (Decision 5). Verify with a new test using a fake repository whose `RecordFileVersion` throws: `Execute` throws that exception, no transfer starts after the failure, and the workers have stopped when `Execute` returns.
- [ ] 2.3 Verify graceful cancellation (Decision 6) with a new test that requests cancellation while several transfers run at `transfer_concurrency` above 1. Every transfer that started is recorded before `Execute` returns, and no new transfer starts. Confirm `BackupPipelineTests`' existing cancellation tests also pass unchanged.

## 3. Threading Guarantees

- [ ] 3.1 Add a test with a fake repository and manifest batch that record `Environment.CurrentManagedThreadId`, asserting that every `RecordFileVersion` and `Commit` call during `Execute` happens on the test's calling thread, with `transfer_concurrency` above 1. Running it from an xUnit test thread also shows the synchronous recorder wait does not deadlock.
- [ ] 3.2 Update `BackupExecutor`'s class and method doc comments to describe the result/recorder model instead of "serialized under a lock". Verify by reading the diff for accuracy.

## 4. Verification

- [ ] 4.1 Run the full test suite, including the real-repository integration tests (`BackupExecutorCheckpointRealRepositoryTests`, `BackupPipelineRealRepositoryTests`), with `dotnet test Vara.slnx` and verify all tests pass.
- [ ] 4.2 Run `BackupTransferBenchmarks` before and after the change (`dotnet run -c Release --project benchmarks/Vara.Benchmarks -- --filter "*BackupTransfer*"`) and verify no throughput regression at concurrency 1 and above 1.
- [ ] 4.3 Publish the CLI with Native AOT (`dotnet publish src/Vara.Cli -c Release`) and verify no new trim/AOT warnings and that a single standalone `vara.exe` is still produced (`portable-distribution`).
- [ ] 4.4 Manually run a real backup with `transfer_concurrency` above 1, press Ctrl+C mid-transfer, and verify the snapshot is recorded as `Cancelled` and that the next run re-detects only uncommitted files.
