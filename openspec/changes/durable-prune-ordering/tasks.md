## 1. Tests first

- [ ] 1.1 In `tests/Vara.Engine.Tests/Runs/MaintenanceEngineTests.fs`, add a module named `Snapshot removal is durable before content is removed` with the test `Snapshot removal is on persistent storage before the first content removal`: build a target with two snapshots and an unreferenced blob (reuse that file's existing helpers), run `PruneRun.execute` with a `DeleteBlob` effect that, on its first call, copies only `profile.db` (not the `-wal` file) to a scratch path and asserts the removed snapshot's rows are absent from that copy. Verify the test fails against the current code.
- [ ] 1.2 In the same module add `No remaining record references removed content after an abrupt loss`: the `DeleteBlob` effect copies the main file on every call and asserts that no row in the copy references any blob already deleted. Verify it fails against the current code.

## 2. Implementation

- [ ] 2.1 In `src/Vara.Engine/Storage/SqliteManifest.fs`, extract the `PRAGMA wal_checkpoint(TRUNCATE)` execution used by `checkpoint` into one private function and call it from `checkpoint`. Verify the existing manifest and backup tests still pass.
- [ ] 2.2 In `removeSnapshots`, set `PRAGMA synchronous = FULL` before `BeginTransaction`, commit, run the shared WAL-folding function, and restore `PRAGMA synchronous = NORMAL` in a `finally`. Leave the early return for an empty id list untouched. Verify tests 1.1 and 1.2 now pass.
- [ ] 2.3 In `src/Vara.Engine/Runs/PruneRun.fs`, correct the comment above `Prune.blobsToRemove` so it says the rows are durably gone before any blob is deleted, which is what makes a crash leave only orphans. Verify the build has no warnings.

## 3. Verification

- [ ] 3.1 Run `dotnet build Vara.slnx` (no warnings) and `dotnet test Vara.slnx`, and verify everything passes. The test `History command corpus` in `Vara.Cli.Tests` fails intermittently on the untouched baseline too (a test-host encoding issue unrelated to this change); note it rather than fixing it here.
- [ ] 3.2 Stop and leave the working tree uncommitted for review. Do not archive or sync the change until the author has reviewed and committed the implementation.
