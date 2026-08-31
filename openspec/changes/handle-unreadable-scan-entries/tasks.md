## 1. Scan-failure data model

- [ ] 1.1 Add a `ScanFailure` record (relative path + reason: unreadable entry vs.
      unenumerable directory) alongside `ScannedEntry` in
      `Vara.Core.Abstractions`, and verify the project builds with the new type in
      place before wiring it up.
- [ ] 1.2 Change `IFileSystemScanner.Scan` (or add a companion member) so a scan
      exposes both the successfully scanned entries and any `ScanFailure`s
      encountered, without breaking the existing lazy `IEnumerable<ScannedEntry>`
      happy path, and update the interface's XML doc to describe the new contract.

## 2. Scanner exception handling fixes

- [ ] 2.1 In `DirectoryFileSystemScanner.Walk`, widen the per-entry
      `File.GetAttributes` catch from `IOException` to
      `IOException or UnauthorizedAccessException`, and emit a `ScanFailure` for
      that entry instead of silently `continue`-ing.
- [ ] 2.2 In `DirectoryFileSystemScanner.ToEntry`, widen the `File.GetAttributes`
      catch the same way, falling back safely (as today) while also surfacing a
      `ScanFailure` for the entry rather than only defaulting its attributes.
- [ ] 2.3 In `DirectoryFileSystemScanner.TryGetLinkTarget`, widen the catch the
      same way so a permission-denied reparse point cannot throw uncaught.
- [ ] 2.4 In `DirectoryFileSystemScanner.Walk`'s outer directory-enumeration catch,
      stop discarding the failure: emit one `ScanFailure` for the directory
      (subtree root) instead of a bare `yield break`, and verify via a manual
      trace that a nested unreadable directory reports exactly one failure, not
      one per file that would have been inside it (per design.md's risk
      mitigation).
- [ ] 2.5 Verify `dotnet build` succeeds for `Vara.Infrastructure` after the above
      changes.

## 3. Pipeline integration

- [ ] 3.1 Update `BackupPipeline.Run` to collect `ScanFailure`s produced during
      scanning (alongside the scanned entries consumed by `BackupDiffer.Diff`).
- [ ] 3.2 Merge scan failures with `BackupExecutor`'s existing per-file failures
      when building `SnapshotStats` (`FilesFailed`) and `BackupRunResult`
      (`FailedPaths`), so both failure sources land in the same counters/list.
- [ ] 3.3 Confirm (by reading `BackupRunSummaryFormatter.Format`) that a scan
      failure folded into `BackupRunResult.FailedPaths` now appears in the CLI's
      "Failed (N):" summary section with no formatter changes required.

## 4. Tests

- [ ] 4.1 Add a `DirectoryFileSystemScannerTests` case that denies read access to a
      single file (e.g. via an ACL deny rule applied and removed within the test)
      and verifies the scan completes, reports a `ScanFailure` for that file, and
      still returns the other files.
- [ ] 4.2 Add a case that denies access to enumerate a subdirectory and verifies
      the scan completes, reports exactly one `ScanFailure` for that directory,
      and still returns entries from sibling directories outside the subtree.
- [ ] 4.3 Add a `BackupPipeline`/`BackupExecutor`-level test (or extend an existing
      one) verifying a scan-time failure ends up in `BackupRunResult.FailedPaths`
      and `SnapshotStats.FilesFailed`, and that the run still completes and
      commits a snapshot rather than failing it.
- [ ] 4.4 Run the full test suite (`dotnet test`) and verify all tests pass,
      including the new cases above.
