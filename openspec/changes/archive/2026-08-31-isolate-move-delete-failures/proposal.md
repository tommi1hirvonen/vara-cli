## Why

`BackupExecutor.ExecuteMoveOrDelete` calls `IContentStore.MoveMirrorEntry` / `RemoveFromMirror` with no try/catch, while `ExecuteTransfer` (Add/Change) catches `IOException`/`UnauthorizedAccessException` per file and records a failed path instead of aborting. If a mirror-side file is locked (AV scan, open handle) or access is denied during a move/delete, the exception propagates out of `BackupExecutor.Execute`, `BackupPipeline.Run`'s outer catch marks the whole snapshot Failed and rethrows, and the entire backup run aborts — for a single locked file that Add/Change would have shrugged off as one failed path. This is inconsistent with the resilience posture the backup-execution spec already establishes for scan/transfer failures.

## What Changes

- Wrap each Move/Delete operation in `BackupExecutor.ExecuteMoveOrDelete` with the same `IOException`/`UnauthorizedAccessException` catch used by `ExecuteTransfer`, recording the affected path to `failedPaths` and incrementing the failed count instead of throwing.
- A failed Move degrades to a Delete-of-old-path failure record (the file could not be relocated); a failed Delete records the path as failed. Neither aborts the run or corrupts manifest state — no partial/inconsistent `RecordFileVersion` calls are made for an operation that failed partway.
- Extend the `backup-execution` spec's "Unreadable files do not abort the run" requirement (or add a sibling requirement) to explicitly cover mirror-side lock/permission failures encountered during move or delete, not just source-side read failures during scan/transfer.

## Capabilities

### New Capabilities
(none)

### Modified Capabilities
- `backup-execution`: fault isolation for per-file failures now explicitly covers Move and Delete operations against the mirror (not just Add/Change source reads), so a locked or inaccessible mirror entry is recorded as a failed path rather than aborting the entire run.

## Impact

- `src/Vara.Application/Backup/BackupExecutor.cs`: `ExecuteMoveOrDelete` gains per-operation try/catch and failure recording; needs access to `failedPaths`/`reportLock`/`Counts` the same way `ExecuteTransfer` does.
- No public API/CLI surface changes. `BackupRunResult.FailedPaths` may now include move/delete failures in addition to scan and transfer failures (it's already a general-purpose list, so no shape change).
- Existing unit tests for `BackupExecutor` covering Move/Delete happy paths remain valid; new tests needed for locked-mirror-entry failure scenarios.
