## 1. Core model changes

- [x] 1.1 Add `ScanFailureReason.SourceUnavailable` to `Vara.Core.Abstractions.IFileSystemScanner` and update its doc comment; verify the project builds
- [x] 1.2 Add a new field to `ScanFailure` carrying the failure's mirror-space path (e.g. `MirrorPath`), computed via `AbsolutePathMirrorMapper.ToMirrorPath` at each construction site, alongside the existing source-relative `RelativePath`; update its doc comment to explain the two path spaces and verify the project builds

## 2. Scanner changes

- [x] 2.1 In `DirectoryFileSystemScanner.ScanSource`, when a source's path is neither an existing file nor an existing directory, record a `ScanFailure` with `ScanFailureReason.SourceUnavailable` (mirror path = `ToMirrorPath(source.Path)`) instead of silently `yield break`ing; verify with a new scanner test asserting exactly one `SourceUnavailable` failure and zero entries for a non-existent source path
- [x] 2.2 Populate the new mirror-path field at the existing `UnreadableDirectory` failure site in `Walk` (mirror path = `ToMirrorPath` of the denied directory's absolute path) and the existing `UnreadableEntry` failure sites in `Walk`/`ToEntry` (mirror path = `ToMirrorPath` of the unreadable entry's absolute path); verify existing scanner tests (`DirectoryFileSystemScannerTests`) still pass and extend the permission-denied-directory test to assert the new field's value

## 3. Differ changes

- [x] 3.1 Change `BackupDiffer.Diff`'s signature to accept the scan's failures (e.g. `IReadOnlyList<ScanFailure>`) in addition to `scanned` and `currentState`
- [x] 3.2 Implement suppression: before computing `DeletedPaths`, exclude any `currentState` key that equals, or is nested under (segment-boundary-aware, case-insensitive), any failure's mirror path - reusing the same `equals`-or-`starts-with(prefix + separator)` comparison idiom used by `DirectoryFileSystemScanner.IsExcluded`
- [x] 3.3 Verify with new `BackupDifferTests` cases: (a) a failure whose mirror path exactly matches a current-state path suppresses that path from `DeletedPaths`; (b) a failure whose mirror path is an ancestor directory suppresses every current-state path nested under it; (c) a current-state path that shares a prefix but not a full path segment with a failure's mirror path (e.g. `C\Users\john2\...` vs. failure `C\Users\john`) is NOT suppressed; (d) a current-state path unrelated to any failure is still classified deleted as before

## 4. Pipeline wiring

- [x] 4.1 Update `BackupPipeline.Run` to pass `scanResult.Failures` into `BackupDiffer.Diff`; verify the project builds and existing `BackupPipelineTests`/`BackupPipelineRealRepositoryTests`/`BackupPipelineRealContentStoreTests` still pass

## 5. Integration coverage

- [x] 5.1 Add an integration test (alongside `BackupPipelineRealRepositoryTests`/`BackupPipelineRealContentStoreTests`) that runs a real backup for a profile whose source path doesn't exist on disk (simulating an unplugged drive/typo) against a manifest with pre-existing tracked files, and verifies: the run completes, reports a `SourceUnavailable` failure, `FilesDeleted` is 0, and every previously mirrored file for that source still exists in the mirror
- [x] 5.2 Add an integration test covering the permission-denied-directory case end-to-end (scan -> diff -> plan -> execute) verifying previously tracked files under the denied subtree are neither deleted from the mirror nor removed from current manifest state, using the same real-ports test fixtures as task 5.1

## 6. Documentation

- [x] 6.1 Review CLI-facing docs/help text (if any) describing backup failure reporting or deletion behavior, and update them to mention that a scan failure suppresses deletion for the affected path/subtree/source, only if such documentation already exists and would otherwise be inaccurate
