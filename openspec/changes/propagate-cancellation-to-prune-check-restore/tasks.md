## 1. Command wiring

- [x] 1.1 Add a `CancellationToken cancellationToken = default` parameter to `PruneCommand.Create`, `CheckCommand.Create`, and `RestoreCommand.Create` in `src/Vara.Cli/Commands/`, mirroring `BackupCommand.Create`'s existing parameter, and verify the project still builds
- [x] 1.2 Update `src/Vara.Cli/Program.cs` to pass `gracefulCancellation.TokenSource.Token` to `PruneCommand.Create`, `CheckCommand.Create`, and `RestoreCommand.Create`, and verify a manual run of each command still starts and completes normally with no Ctrl+C pressed

## 2. Prune cancellation

- [x] 2.1 Thread the cancellation token from `PruneCommand`'s action into `PruneService.Prune`, checking `IsCancellationRequested` once before retention evaluation/snapshot removal starts (skipping that atomic step entirely if already cancelled) and between each blob garbage-collected, returning a `Cancelled` flag on `PruneResult` rather than throwing
- [x] 2.2 At the point `PruneCommand`'s action calls `PruneService.Prune`, report a "prune was cancelled" outcome (not an error) when the result's `Cancelled` flag is set, and exit successfully; verify with a unit/integration test that cancels before the run starts and one that cancels mid-garbage-collection, asserting the reported outcome and exit code
- [x] 2.3 Verify with a test that work already committed before cancellation (a fully-applied snapshot removal step, blobs already garbage-collected) remains committed and is not rolled back

## 3. Check cancellation

- [x] 3.1 Thread the cancellation token from `CheckCommand`'s action into `IntegrityCheckService.Check`, checking `IsCancellationRequested` between each referenced blob verified and returning a `Cancelled` flag on `IntegrityCheckResult` rather than throwing
- [x] 3.2 At the point `CheckCommand`'s action calls `IntegrityCheckService.Check`, report a "check was cancelled" outcome (not an error) when the result's `Cancelled` flag is set, while still reporting any problems already found before cancellation, and exit successfully; verify with a unit/integration test

## 4. Recursive restore cancellation

- [x] 4.1 Thread the cancellation token from `RestoreCommand`'s recursive-restore path into the directory-restore executor's loop over the planned `DirectoryRestorePlan`, checking `IsCancellationRequested` between each planned path written or removed and returning a cancelled indication rather than throwing
- [x] 4.2 At the point `RestoreCommand`'s recursive path invokes the executor, report a "restore was cancelled" outcome (not an error) when cancellation occurred, and exit successfully; verify with a unit/integration test that cancels mid-plan and asserts paths already applied remain in place while unapplied paths are not written/removed

## 5. Second Ctrl+C behavior

- [x] 5.1 Verify with a test (or manual run, documented in the PR) that a second Ctrl+C during `prune`, `check`, or `restore --recursive` still forces immediate termination via the existing `GracefulCancellation` second-signal path, with no change required to `GracefulCancellation` itself

## 6. Regression coverage

- [x] 6.1 Run the existing `PruneCommand`/`CheckCommand`/`RestoreCommand` test suites and verify all pass with no behavior change when no cancellation occurs
