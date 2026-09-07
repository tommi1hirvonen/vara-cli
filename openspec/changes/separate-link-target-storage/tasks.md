## 1. Schema and core model

- [x] 1.1 Add `link_target TEXT NULL` to the `file_versions` `CREATE TABLE IF NOT EXISTS` in `SqliteSnapshotRepository`, and make `content_hash` nullable in that same definition; verify a fresh `profile.db` created by the test suite has both columns with the expected nullability via `sqlite_master`/`PRAGMA table_info`.
- [x] 1.2 Make `FileVersionRecord.ContentHash` nullable and add a nullable `LinkTarget`; make `CurrentFileState.ContentHash` nullable and add a nullable `LinkTarget` alongside the existing `IsLinked`; verify the solution builds with `dotnet build Datateal.slnx` equivalent for this repo (`dotnet build` at the solution root) with no remaining non-nullable-usage warnings/errors at the two records' call sites.

## 2. Recording a link and mirror cleanup

- [x] 2.1 Update `BackupExecutor.ExecuteMetadataOnlyOperation`'s `Link` branch to write `content_hash = NULL` and `link_target = LinkTarget` (or `NULL` if unavailable) instead of writing the target into `content_hash`; verify with a `BackupExecutorTests` case asserting the recorded row's `ContentHash` is null and `LinkTarget` matches the scanned target.
- [x] 2.2 Update `SqliteSnapshotRepository.RecordFileVersion` (and its `INSERT` statement/parameters) to accept and persist `link_target`, and to write `content_hash` as `NULL` when not provided instead of an empty string; verify with a `SqliteSnapshotRepositoryTests` round-trip test that a `Linked` row reads back with `ContentHash == null` and the expected `LinkTarget`.
- [x] 2.3 In `BackupExecutor`'s `Link` branch, detect a file-to-link transition (the path's previous `CurrentFileState.IsLinked` was false) and call `contentStore.RemoveFromMirror` for the path's previous content hash before recording the `Linked` row, mirroring the existing `Delete` branch; verify with a `BackupExecutorTests`/`BackupPipelineTests` case that a tracked regular file replaced by a symlink no longer leaves its old mirrored copy on disk after the run.
- [x] 2.4 In `BackupExecutor`'s `Delete` branch, skip the `RemoveFromMirror` call when `KnownContentHash` is null/empty (deleting a path whose most recent state was itself `Linked`) instead of passing a missing hash through; verify with a test that deleting a previously-linked path completes without calling into the content store's blob path and without throwing.

## 3. Content-store safety guard

- [x] 3.1 Add an explicit guard in `FileSystemContentStore.BlobPath` that throws a clear, descriptive exception (not `ArgumentOutOfRangeException`) for a null or empty hash; verify with a `FileSystemContentStoreTests` case asserting the specific exception type/message instead of the raw indexing exception.

## 4. `vara check` false-positive fix

- [x] 4.1 Update `SqliteSnapshotRepository.GetAllReferencedContentHashes` to filter `WHERE content_hash IS NOT NULL`; verify with a test asserting a profile containing a `Linked` row does not include that row's (now-null) value in the returned set.
- [x] 4.2 Verify end-to-end with an `IntegrityCheckServiceTests` case: a manifest containing a `Linked` entry and otherwise-intact content reports zero missing/corrupt findings.

## 5. Single-file restore fix

- [x] 5.1 Add a new specific exception type (e.g. `RestoreLinkedEntryException` or similar, following this codebase's existing restore-exception naming) reported the same way other clear-error restore cases are (see `NoHistoryForPathException`/`DestinationExistsException` handling in `RestoreCommand`); verify it renders a clear, non-stack-trace message when surfaced through `ErrorReporting`.
- [x] 5.2 Update `SnapshotHistoryService.RestoreVersion`/`RestoreAsOf` to check the resolved record's `ChangeKind`/`IsLinked` and throw the new exception instead of calling `contentStore.ExtractTo` when it is `Linked`; verify with `SnapshotHistoryServiceTests` cases for both `RestoreVersion` and `RestoreAsOf` resolving to a `Linked` version.
- [x] 5.3 Update the interactive version picker in `RestoreCommand` to filter out `ChangeKind == Linked` alongside the existing `ChangeKind != Deleted` filter; verify with a `RestoreCommand`/CLI-level test that a path whose only non-deleted history is a `Linked` version reports "no history to restore" rather than offering it in the picker.

## 6. Recursive restore fix

- [x] 6.1 Add a `Skipped` (or similarly named) list to `DirectoryRestorePlan`, populated by `PlanDirectoryRestore` with historical entries whose `CurrentFileState`/`FileVersionRecord` is `IsLinked`, excluded from `ToWrite`; verify with a `SnapshotHistoryServiceTests` case that a directory containing a tracked symlink produces a plan whose `ToWrite` excludes it and whose `Skipped` list includes it.
- [x] 6.2 Update `ExecuteDirectoryRestore` to leave skipped entries untouched (no `ExtractTo` call, no removal) and confirm the existing `ToRemove` pass is unaffected by a `Linked` current entry (it contributes to neither `ToWrite` nor `ToRemove`); verify with a test that running the plan performs no I/O for a skipped entry and does not throw.
- [x] 6.3 Update `RestoreCommand`'s recursive-restore output/confirmation text to report the skipped-link count alongside the existing written/removed counts (e.g. "N file(s) written, M removed, K link(s) skipped"); verify with a CLI-level test asserting the skipped count appears in the summary when the plan has skipped entries, and is omitted or zero otherwise.

## 7. Full-suite verification

- [x] 7.1 Run the full test suite (`dotnet test`) and confirm all tests pass, including any existing symlink-related tests in `BackupExecutorTests`, `BackupDifferTests`, `IntegrityCheckServiceTests`, `SnapshotHistoryServiceTests`, `SqliteSnapshotRepositoryTests`, `FileSystemContentStoreTests`, `CheckCommandTests`, and the integration tests under `Vara.IntegrationTests`.
- [x] 7.2 Add or extend an integration test (`Vara.IntegrationTests`) covering the full lifecycle: back up a source containing a symlink and a to-be-linked regular file, run `vara check`, run `restore --recursive`, and confirm no false-missing findings, no partial restore, and no stale mirror copy - using real ports rather than fakes.
