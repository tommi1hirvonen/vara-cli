## 1. Exception preservation

- [ ] 1.1 In `BackupPipeline.Run`'s catch block, wrap `repository.FailSnapshot(...)` and `manifestBatch.Commit()` so a secondary exception from either does not replace the original, and verify a unit test that forces `Commit()` to throw inside the catch path asserts the original exception is what propagates
- [ ] 1.2 Log or otherwise surface the secondary exception (if any) rather than silently discarding it, and verify a test confirms it is observable through the existing diagnostics channel

## 2. Progress teardown ordering

- [ ] 2.1 Introduce a mechanism for `BackupCommand`'s progress reporting to track outstanding queued deliveries (e.g. a wrapper around `Progress<BackupProgress>` or an explicit counter/semaphore), and verify a unit test confirms it correctly tracks in-flight report counts
- [ ] 2.2 Add a bounded drain/wait after `pipeline.Run` returns (success or throw) and before the `Progress().Start(ctx => ...)` lambda exits, and verify a test simulating a delayed final report confirms teardown waits for it (up to the bound) before proceeding
- [ ] 2.3 Verify exceeding the drain's bound does not hang or fail the command - it proceeds with teardown - via a test forcing the bound to be exceeded

## 3. Verification

- [ ] 3.1 Run `openspec validate --change fix-backup-pipeline-exception-and-progress-teardown --strict` and confirm it passes
- [ ] 3.2 Run the full `Vara.Application.Tests` and `Vara.Cli.Tests` (or equivalent) suites and confirm no existing tests regress
