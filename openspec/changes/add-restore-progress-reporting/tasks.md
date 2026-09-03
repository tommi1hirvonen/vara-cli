## 1. Content store: incremental `ExtractTo`

- [ ] 1.1 Add `Action<long>? onBytesCopied = null` to `IContentStore.ExtractTo`'s signature and update its XML doc, and verify the project builds with all implementers updated
- [ ] 1.2 Update `FileSystemContentStore.ExtractTo` to delete an existing destination file (if present) then call the existing `CopyWithProgress` helper instead of `File.Copy`, and verify `FileSystemContentStoreTests` covers: a fresh destination, an overwritten existing destination, and `onBytesCopied` receiving incremental chunk sizes summing to the file's total size
- [ ] 1.3 Verify no other `ExtractTo` caller breaks by building the full solution (the new parameter is optional/defaulted)

## 2. Service layer: forwarding progress

- [ ] 2.1 Add an optional progress callback parameter to `SnapshotHistoryService.RestoreAsOf` and `RestoreVersion`, forwarded to `contentStore.ExtractTo`, and verify a unit test asserts the callback is invoked with incremental byte counts during a restore of a multi-chunk file

## 3. CLI: live and plain progress display

- [ ] 3.1 In `RestoreCommand`, branch on `OutputMode.IsLiveCapable(console)` the same way `BackupCommand` does, and implement `RunWithLiveDisplay`/`RunWithPlainOutput`-equivalent paths for restore
- [ ] 3.2 Implement the live path using `console.Progress().Columns(new TaskDescriptionColumn(), new ProgressBarColumn(), new PercentageColumn())` (add `RemainingTimeColumn`/`TransferSpeedColumn` if the design's ETA/throughput goal is kept), with the task's `MaxValue` set from the resolved version's known `Size` before extraction begins, and verify a manual run against a large restored file shows a bar advancing smoothly rather than jumping only at completion
- [ ] 3.3 Implement the plain-output path printing periodic byte/percentage lines analogous to `BackupCommand.RunWithPlainOutput`, and verify a unit test or manual redirected-output run produces plain text lines with no ANSI/live redraw
- [ ] 3.4 Reuse `Backup.BackupProgress` + `ProgressDisplayGate` to rate-limit redraws for both paths, and verify a unit test confirms restoring a file that triggers many small `onBytesCopied` calls does not redraw on every single call

## 4. Verification

- [ ] 4.1 Run `dotnet test` for `Vara.Application.Tests`, `Vara.Infrastructure.Tests`, and `Vara.Cli.Tests`, and confirm all tests pass
- [ ] 4.2 Manually restore a small file and confirm no distracting flash/flicker from the progress bar appearing and disappearing almost instantly
- [ ] 4.3 Manually restore a large file (large enough to take a few seconds) and confirm the progress bar, percentage, and (if implemented) throughput/ETA advance visibly during the copy
