## 1. Extend BackupExecutor with transfer lifecycle callbacks

- [x] 1.1 Add optional `Action<string>? onFileStarted` parameter to `BackupExecutor.Execute` signature (nullable, defaulting to null) and invoke it at the start of `ExecuteTransfer`, before `File.OpenRead`, passing `operation.SourceAbsolutePath`. Verify existing `BackupExecutorTests` still compile and pass without modification.
- [x] 1.2 Add optional `Action<string>? onFileFinished` parameter to `BackupExecutor.Execute` and invoke it from `ExecuteTransfer`'s `finally` block with `operation.SourceAbsolutePath`, on both success and failure. Verify a failed transfer still invokes the callback without incrementing the successful-file callback.

## 2. Add ActivePaths to BackupProgress and BackupProgressState

- [x] 2.1 Add `IReadOnlyList<string>? ActivePaths` to `BackupProgress` record with a default of `null`. Verify existing tests that construct `BackupProgress` directly still compile without modification.
- [x] 2.2 Add `IReadOnlyList<string>? ActivePaths` field to `BackupProgressState` struct. Update `ToProgress()` to include the field in the reconstructed `BackupProgress`. Verify `BackupProgressColumnTests` (if any) still compile and pass.

## 3. Maintain in-flight set in BackupPipeline

- [x] 3.1 Declare a `ConcurrentDictionary<string, byte> inFlight` local in `BackupPipeline.Run`. Pass `onFileStarted: path => inFlight.TryAdd(path, 0)` and `onFileFinished: path => inFlight.TryRemove(path, out _)` to `BackupExecutor.Execute`; keep the existing `onFileTransferred` lambda responsible for successful-file counting and reporting only. Verify unit tests for `BackupPipeline` still pass.
- [x] 3.2 In every `progress?.Report(new BackupProgress(...))` call within `BackupPipeline.Run`, add `inFlight.Keys.ToArray()` as the `ActivePaths` argument. This covers the `onTransferPhaseStarting` report (empty snapshot), the `onBytesTransferred` report, and the `onFileTransferred` report. Verify via integration test or manual inspection that `ActivePaths` is populated during a transfer.

## 4. Thread through BackupCommand wiring

- [x] 4.1 In `BackupCommand.RunWithLiveDisplay`, update the `BackupProgressState` construction inside the `Render` closure to include `ActivePaths` from the `admitted` progress value. Verify the project compiles.
- [x] 4.2 In `BackupCommand.RunWithPlainOutput`, no change is required — the plain path omits active-file display by design (per design.md Non-Goals). Leave it unchanged and confirm compilation.

## 5. Render active paths in BackupProgressColumn

- [x] 5.1 Update `BackupProgressColumn.BuildStatsRow` (and `RenderStats` if needed) to read `progress.ActivePaths`. For each of the first three entries, call `PathLabelTruncator.Truncate(path, 48)` and append a full-width row to the stats `Grid`. When `ActivePaths.Count > 3`, append a `+ N more` row (where N = `ActivePaths.Count - 3`) instead of the excess entries. When `ActivePaths` is null or empty, append no additional rows. Verify by running the backup command against a real profile with at least one file to transfer and confirming the active path appears below the stats rows.

## 6. Tests

- [x] 6.1 Add unit tests for the `RenderStats`/`BuildStatsRow` active-paths rendering: (a) null/empty `ActivePaths` produces no extra rows, (b) 1–3 paths produce exactly that many rows with truncated labels, (c) > 3 paths produces three rows plus a `+ N more` row. Verify all new tests pass.
- [x] 6.2 Add or extend a unit test for `BackupPipeline` verifying that `ActivePaths` in reported `BackupProgress` values reflects the in-flight set at the time of each report (e.g., using a fake `onFileStarted`/`onFileTransferred` ordering). Verify the test passes.
