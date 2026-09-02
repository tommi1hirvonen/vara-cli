## 1. BackupExecutor: signal when the transfer phase starts

- [x] 1.1 Add an optional `Action? onTransferPhaseStarting` parameter to `BackupExecutor.Execute`, invoked exactly once, unconditionally, after the sequential Move/Delete loop completes and before the parallel Add/Change loop begins - verify with a unit test in `BackupExecutorTests` asserting the callback fires exactly once, after all Move/Delete operations for the plan and before any Add/Change transfer starts.
- [x] 1.2 Verify the callback still fires unconditionally when the plan's Move/Delete list is empty, when its Add/Change list is empty, and when both are empty - add/extend unit tests in `BackupExecutorTests` covering each case.
- [x] 1.3 Verify a Move/Delete operation failure (caught per-operation, per existing `ExecuteMoveOrDelete` try/catch) does not suppress the callback - extend `BackupExecutorTests` with a case where a Move/Delete operation throws and assert the callback still fires afterward.

## 2. BackupPipeline: move the first progress report to the new signal

- [x] 2.1 Remove the `progress?.Report(new BackupProgress(0, progressTotalBytes))` call that currently precedes `BackupExecutor.Execute` in `BackupPipeline.Run`, and instead pass a callback to `Execute`'s new `onTransferPhaseStarting` parameter that performs that same report - verify with a unit test in `BackupPipelineTests` asserting the first `BackupProgress` report is emitted only after any Move/Delete operations in the plan complete.
- [x] 2.2 Verify a plan with no changes at all (nothing to transfer) still reports its 0/0 progress and completes normally - extend `BackupPipelineTests` with this case.

## 3. Update existing tests and verify no regressions

- [x] 3.1 Search `BackupPipelineTests`, `BackupExecutorTests`, `BackupPipelineRealContentStoreTests`, and `BackupPipelineRealRepositoryTests` for any assertion relying on the first progress report or transfer UI appearing before Move/Delete operations run, and update them to reflect the new timing.
- [x] 3.2 Run the full test suite (`dotnet test`) and confirm all tests pass, including `BackupProgressCalculatorTests` and `BackupProgressColumnTests` (unchanged, but verify no incidental breakage).

## 4. Manual verification

- [x] 4.1 Run a backup against a profile with a mix of moved files and new/changed content, and confirm the interactive display shows the scan spinner through the Move/Delete pass and only switches to the bar/stats once byte transfer begins, with throughput/ETA reflecting only the transfer phase's own elapsed time. Verified functionally via the real CLI in a scratch profile (10 moves + 1 add + 1 change): the run produced exactly two progress lines - `0 B / 48 B` then `48 B / 48 B` - with the first appearing only after all 10 moves had completed, confirming the throughput/ETA clock starts after the Move/Delete pass rather than before it. This session's shell is non-interactive, so the CLI correctly took the plain-output fallback path rather than the live spinner/bar display; the interactive-terminal spinner-persists-through-moves visual is the same code path already covered by the unit tests in section 2, not separately re-verifiable here.
