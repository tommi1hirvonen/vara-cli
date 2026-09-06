## 1. Manifest change kind

- [ ] 1.1 Add a new `FileChangeKind` value (e.g. `Linked`) and confirm the build succeeds with all existing exhaustive switches over `FileChangeKind` updated to handle it explicitly
- [ ] 1.2 Add a corresponding `PendingChangeKind` (or reuse an existing mechanism) so `BackupPlanner` can produce a pending change for a linked path, and verify a unit test on `BackupPlanner` asserts a link entry produces the new manifest row

## 2. Diff fix

- [ ] 2.1 In `BackupDiffer.Diff`, add a link's `RelativePath` to `scannedPaths` before (or regardless of) the `IsLink` early-continue, and verify a unit test on `BackupDiffer` asserts a symlink path is never included in the deleted-paths result
- [ ] 2.2 Add a unit test asserting a path previously tracked as a regular file, now scanned as a symlink, is classified as `Linked` rather than deleted
- [ ] 2.3 Add a unit test asserting a symlink present, unchanged, across two consecutive diffs is not classified as added, deleted, or changed on the second run

## 3. Integration verification

- [ ] 3.1 Add an end-to-end `BackupPipeline`/`BackupExecutor` test running two backup passes over a source containing a symlink, and verify the manifest records the link and no false deletion occurs
- [ ] 3.2 Run `openspec validate --change fix-symlink-scan-recording --strict` and confirm it passes
- [ ] 3.3 Run the full `Vara.Application.Tests` suite and confirm no existing tests regress
