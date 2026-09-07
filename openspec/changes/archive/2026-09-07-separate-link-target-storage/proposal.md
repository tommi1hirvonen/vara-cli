## Why

`file_versions.content_hash` is overloaded: for `Linked` rows it holds the symlink/junction's
target path instead of a content-store hash (`BackupExecutor.ExecuteMetadataOnlyOperation`),
because the original symlink-recording change deliberately reused the existing column rather than
adding one. Every downstream consumer that reads `content_hash` assumes it is always a real hash,
so this now causes three confirmed, user-visible bugs: `vara check` reports every tracked
symlink/junction as a **missing blob** and exits non-zero on any profile that has one; `restore
--recursive` throws a raw `FileNotFoundException` on a Linked entry *after* its `ToRemove`
deletions have already run, leaving a partial restore; and `RemoveFromMirror`/`BlobPath` can throw
an unhandled `ArgumentOutOfRangeException` (aborting the whole backup run) or, with a rooted path
as `hash`, toggle `ReadOnly` on a file outside the target root entirely. Additionally, a tracked
regular file replaced by a symlink produces a `Linked` op that performs no mirror I/O, so its
former mirrored copy is silently orphaned on disk forever - the mirror stops mirroring reality for
that path.

The project is pre-release with no shipped databases to preserve, so this is the right time to
fix the storage model directly (a dedicated `link_target` column) rather than continuing to patch
every consumer against a shared, ambiguous column - with no migration path required.

## What Changes

- Add a `link_target TEXT NULL` column to `file_versions`; `content_hash` becomes `NULL` for
  `Linked` rows instead of holding the link's target path (or an empty string). **BREAKING**
  (manifest schema shape changes; acceptable pre-release, no migration provided).
- `BackupExecutor` writes a `Linked` row's target into `link_target` and leaves `content_hash`
  `NULL`, instead of writing the target into `content_hash`.
- `SqliteSnapshotRepository.GetAllReferencedContentHashes`/`GetPathsForContentHash` and
  `GetCurrentState`/`GetStateAsOf` (`CurrentFileState`) no longer expose a link's target through
  `ContentHash`; `CurrentFileState`/`FileVersionRecord` gain a nullable `LinkTarget` alongside a
  nullable `ContentHash`.
- `vara check` (`IntegrityCheckService`/`GetAllReferencedContentHashes`) no longer considers
  `Linked` rows as referenced blobs, so a tracked symlink/junction no longer produces a false
  "missing" finding.
- `restore --recursive` (`PlanDirectoryRestore`/`ExecuteDirectoryRestore`) excludes `Linked`
  entries from content extraction, reports how many were skipped as part of the plan/outcome, and
  runs `ToRemove` deletions and `ToWrite` extractions such that a skip never leaves a partial,
  half-applied state.
- Single-file `restore`/`restore --version`/`restore --at` reports a clear, specific error instead
  of a raw `FileNotFoundException` when the resolved version is a `Linked` entry, and the
  interactive version picker excludes `Linked` versions from its selectable list (mirroring how it
  already excludes `Deleted` versions).
- A path tracked as a regular file that is replaced by a symlink/junction now has its previous
  mirror copy removed as part of recording the `Linked` change, so the mirror no longer retains a
  stale copy of content that the manifest considers superseded by a link.
- `FileSystemContentStore.RemoveFromMirror`/`BlobPath` gain an explicit guard for a missing/empty
  hash (which a `Delete` of a previously-`Linked` path can now legitimately produce, since no
  content-store blob was ever associated with that path) instead of relying on `content_hash`
  always being a well-formed hash.

## Capabilities

### New Capabilities
(none)

### Modified Capabilities
- `backup-execution`: symlink/junction recording stores its target in a dedicated field rather
  than the content-hash field, and a file-to-link transition removes the file's now-stale mirror
  copy.
- `backup-integrity`: `vara check` excludes link entries from the referenced-blob set it verifies,
  so a tracked symlink/junction is never reported as a missing blob.
- `snapshot-history`: restoring a `Linked` entry (single-file or as part of a recursive directory
  restore) is handled explicitly - reported/skipped rather than crashing or silently producing a
  partial restore - and the interactive version picker excludes `Linked` versions.

## Impact

- Schema: `Vara.Infrastructure/Snapshots/SqliteSnapshotRepository.cs` (`file_versions` table
  definition and every query touching `content_hash` for a `Linked`-capable row set).
- `Vara.Core/Snapshots/FileVersionRecord.cs` (`FileVersionRecord`, `CurrentFileState`).
- `Vara.Application/Backup/BackupExecutor.cs`, `BackupPlanner.cs`, `BackupDiffer.cs` (link
  recording and mirror cleanup on file-to-link transition).
- `Vara.Application/Integrity/IntegrityCheckService.cs`.
- `Vara.Application/History/SnapshotHistoryService.cs`, `Vara.Cli/Commands/RestoreCommand.cs`.
- `Vara.Infrastructure/Storage/FileSystemContentStore.cs` (`RemoveFromMirror`, `BlobPath`).
- No migration: this is a pre-release project with no shipped/preserved databases, so the schema
  change is applied directly with no upgrade path for existing `profile.db` files.
