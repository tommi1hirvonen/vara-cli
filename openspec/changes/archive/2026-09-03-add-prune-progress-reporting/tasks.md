## 1. Service layer: reporting deletion progress

- [x] 1.1 Add a `PruneProgress(int BlobsDeleted, int TotalBlobs)` record and an `IProgress<PruneProgress>? progress = null` parameter to `PruneService.Prune`, and verify the project builds
- [x] 1.2 Report once before the blob-deletion loop starts (`BlobsDeleted = 0`, `TotalBlobs = unreferencedHashes.Count`), skipping the report entirely when the count is zero, and report again after each blob is deleted, and verify a unit test using a fake `IProgress<PruneProgress>` asserts the expected sequence of reports for a run with several unreferenced blobs
- [x] 1.3 Verify a unit test confirms no report is sent when there are zero unreferenced blobs to delete

## 2. CLI: live and plain progress display

- [x] 2.1 In `PruneCommand`, branch on `OutputMode.IsLiveCapable(console)` the same way `BackupCommand` does
- [x] 2.2 Implement the live path using `console.Progress().Columns(new TaskDescriptionColumn(), new ProgressBarColumn())`, starting one task with `Description = "Evaluating..."` and `IsIndeterminate = true`, and on the first received `PruneProgress` report, set `IsIndeterminate = false`, `MaxValue = TotalBlobs`, `Value = BlobsDeleted`, and `Description = $"Deleting blobs {BlobsDeleted} / {TotalBlobs}"`, updating `Value`/`Description` on each subsequent report, and verify a manual prune run against a profile with several unreferenced blobs shows the spinner followed by an advancing "Deleting blobs x / y" bar
- [x] 2.3 Call `ctx.Refresh()` once after the deletion loop completes (or once the live block ends) so the final count is shown immediately, without adding a per-report rate limiter
- [x] 2.4 Implement the plain-output path printing a message when evaluation starts and a final "Removed N of M blobs" summary line (or periodic count lines, if preferred) analogous to `BackupCommand.RunWithPlainOutput`, and verify a manual redirected-output run produces plain text with no ANSI/live redraw

## 3. Verification

- [x] 3.1 Run `dotnet test` for `Vara.Application.Tests` and `Vara.Cli.Tests`, and confirm all tests pass
- [x] 3.2 Manually run `vara prune <profile>` against a profile with zero unreferenced blobs and confirm the display completes without ever showing a "Deleting blobs 0 / 0" bar
- [x] 3.3 Manually run `vara prune <profile>` against a profile with many unreferenced blobs and confirm the spinner is shown first, followed by a count-based bar that advances to completion
