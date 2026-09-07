## 1. Command wiring

- [ ] 1.1 Add a `CancellationToken cancellationToken = default` parameter to `PruneCommand.Create`, `CheckCommand.Create`, and `RestoreCommand.Create` in `src/Vara.Cli/Commands/`, mirroring `BackupCommand.Create`'s existing parameter, and verify the project still builds
- [ ] 1.2 Update `src/Vara.Cli/Program.cs` to pass `gracefulCancellation.TokenSource.Token` to `PruneCommand.Create`, `CheckCommand.Create`, and `RestoreCommand.Create`, and verify a manual run of each command still starts and completes normally with no Ctrl+C pressed

## 2. Prune cancellation

- [ ] 2.1 Thread the cancellation token from `PruneCommand`'s action into the retention/prune pipeline it calls, and check the token between each snapshot's eligibility evaluation, each snapshot removed, and each blob garbage-collected, throwing/observing `OperationCanceledException` at each check point
- [ ] 2.2 Catch the cancellation at the point `PruneCommand`'s action calls the pipeline, report a "prune was cancelled" outcome (not an error), and exit successfully; verify with a unit/integration test that cancels mid-run (e.g. via a pre-cancelled or cancelled-after-N-iterations token) and asserts the reported outcome and exit code
- [ ] 2.3 Verify with a test that work already committed before cancellation (snapshots already removed, blobs already garbage-collected) remains committed and is not rolled back

## 3. Check cancellation

- [ ] 3.1 Thread the cancellation token from `CheckCommand`'s action into the verification pass, and check the token between each referenced blob verified
- [ ] 3.2 Catch the cancellation at the point `CheckCommand`'s action calls the verification pass, report a "check was cancelled" outcome (not an error) while still reporting any problems already found before cancellation, and exit successfully; verify with a unit/integration test

## 4. Recursive restore cancellation

- [ ] 4.1 Thread the cancellation token from `RestoreCommand`'s recursive-restore path into the directory-restore executor's loop over the planned `DirectoryRestorePlan`, and check the token between each planned path written or removed
- [ ] 4.2 Catch the cancellation at the point `RestoreCommand`'s recursive path invokes the executor, report a "restore was cancelled" outcome (not an error), and exit successfully; verify with a unit/integration test that cancels mid-plan and asserts paths already applied remain in place while unapplied paths are not written/removed

## 5. Second Ctrl+C behavior

- [ ] 5.1 Verify with a test (or manual run, documented in the PR) that a second Ctrl+C during `prune`, `check`, or `restore --recursive` still forces immediate termination via the existing `GracefulCancellation` second-signal path, with no change required to `GracefulCancellation` itself

## 6. Regression coverage

- [ ] 6.1 Run the existing `PruneCommand`/`CheckCommand`/`RestoreCommand` test suites and verify all pass with no behavior change when no cancellation occurs
