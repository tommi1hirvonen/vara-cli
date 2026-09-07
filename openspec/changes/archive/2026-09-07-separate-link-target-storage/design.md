## Context

`file_versions.content_hash` is a single `NOT NULL TEXT` column used for two unrelated purposes:
a real content-store hash for `Added`/`Changed`/`Moved`/`Deleted` rows, and a symlink/junction's
target path (or an empty string) for `Linked` rows (`BackupExecutor.ExecuteMetadataOnlyOperation`).
Every consumer of `content_hash` - `GetAllReferencedContentHashes`, `GetPathsForContentHash`,
`GetCurrentState`/`GetStateAsOf` (`CurrentFileState.ContentHash`), `PlanDirectoryRestore`,
`RestoreVersion`/`RestoreAsOf`, and `FileSystemContentStore.BlobPath`/`RemoveFromMirror` - assumes
it always holds a real hash. `CurrentFileState` already carries a same-purpose `IsLinked` flag
derived from `change_kind`, so `BackupDiffer` can already tell a tracked link apart from tracked
content; the gap is that `content_hash` itself still leaks the wrong kind of value to the several
places that read it directly instead of going through that flag. This is a pre-release project
with no shipped `profile.db` files to preserve, so the fix can change the schema directly with no
migration path.

## Goals / Non-Goals

**Goals:**
- Give link target storage its own column so `content_hash` is unambiguously "a content-store
  hash, or absent" everywhere it is read.
- Make every consumer of a `Linked` row's `ContentHash`/`content_hash` (check, restore, content
  store maintenance) handle its absence explicitly instead of by accident.
- Stop a file-to-link transition from leaving an orphaned mirror copy behind.

**Non-Goals:**
- Recreating a symlink/junction's own target/metadata as a restorable entity (writing an actual
  link at a restore destination) - unchanged from the prior symlink-recording change's stated
  non-goal; a `Linked` entry remains something restore skips and reports, not something restore
  reproduces.
- Any migration or compatibility path for existing `profile.db` files - none exist pre-release, so
  the schema change is applied unconditionally with no `ALTER TABLE`/rebuild-and-swap step (unlike
  the `MigrateFileVersionsCollationIfNeeded` precedent, which exists only because that fix shipped
  after profiles existed in the field).

## Decisions

- **Add `link_target TEXT NULL` to `file_versions`; make `content_hash` nullable.** For a `Linked`
  row, `content_hash` is written as `NULL` and `link_target` carries the symlink/junction's target
  (or `NULL` if the target could not be read). For every other change kind, `link_target` is
  `NULL` and `content_hash` is unchanged. Since there is no existing data to preserve, the table is
  simply declared this way in the `CREATE TABLE IF NOT EXISTS` - no rebuild/copy step is needed.
  - Alternative considered: keep a single column and add an `is_link BOOLEAN` flag instead,
    interpreting `content_hash` as "hash or target depending on the flag." Rejected - it still
    leaves `content_hash` ambiguous at the type level, so a consumer that forgets to check the
    flag reintroduces exactly this bug. A dedicated, separately-typed column makes the invalid
    state (a hash where a path is expected, or vice versa) impossible to read past by accident.
- **`FileVersionRecord`/`CurrentFileState` gain a nullable `LinkTarget` alongside a now-nullable
  `ContentHash`.** `CurrentFileState.IsLinked` is kept (still the cheapest check for "does this
  row have restorable content"), with `LinkTarget` added for callers that need the actual path
  (e.g. a future `browse`/`history` display of what a link points to - out of scope here, but the
  field is added now so it doesn't require another schema change later).
- **`GetAllReferencedContentHashes` filters `WHERE content_hash IS NOT NULL`** rather than
  filtering by `change_kind`. This is simpler than a `change_kind != 'Linked'` predicate and stays
  correct automatically if another non-content change kind is ever introduced.
- **Restore (`PlanDirectoryRestore`, `RestoreVersion`, `RestoreAsOf`) checks `IsLinked`/
  `ChangeKind == Linked` before touching `ContentHash`**, rather than relying on `ExtractTo` to
  fail safely on a bad hash:
  - Single-file restore (`RestoreVersion`/`RestoreAsOf`): if the resolved record is `Linked`,
    throw a new, specific exception (reported the same way other clear-error restore cases already
    are) instead of calling `ExtractTo`. The interactive version picker (`RestoreCommand`) adds
    `ChangeKind != Linked` to its existing `ChangeKind != Deleted` filter, so a user can't select a
    version that would just error.
  - Recursive restore (`PlanDirectoryRestore`): a historical entry with `IsLinked` true is omitted
    from `ToWrite` and added to a new `Skipped` list on `DirectoryRestorePlan`, reported by
    `RestoreCommand` alongside the existing written/removed counts (e.g. "N file(s) written, M
    removed, K link(s) skipped"). It is never added to `ToRemove` either - `ToRemove` is driven by
    "currently live but not live as of the requested date," and a `Linked` current entry follows
    the same current-vs-historical logic as any other live path, just contributing nothing to
    `ToWrite`.
- **File-to-link mirror cleanup happens in `BackupExecutor`'s `Linked` branch**: when the diff
  identifies a file-to-link transition (the path's previous `CurrentFileState` was not already
  `IsLinked`), the executor calls `contentStore.RemoveFromMirror` for that path's previous content
  hash before recording the `Linked` row, exactly as the existing `Delete` branch already does for
  a real deletion. This reuses the same mirror-removal path rather than introducing a second one.
- **`RemoveFromMirror`/`BlobPath` guard against a missing hash.** A `Delete` of a path whose most
  recent state was itself `Linked` (link deleted without ever becoming a regular file again) now
  legitimately has no `KnownContentHash` - there was never a blob for it. `BackupExecutor`'s
  `Delete` branch skips the `RemoveFromMirror` call entirely in that case (there is nothing to
  remove from either the mirror or the content store), and `FileSystemContentStore.BlobPath` gets
  an explicit guard that throws a clear, descriptive exception for a null/empty hash instead of
  letting `hash[..2]` throw `ArgumentOutOfRangeException` - a defense-in-depth check for any other
  future caller, since the known reachable path is now avoided at the call site.

## Risks / Trade-offs

- [`content_hash` becoming nullable touches every query and model that reads it, which is a wide
  blast radius for a single change] → Mitigated by a grep-driven review of every `content_hash`/
  `ContentHash` read site as an explicit task, same approach the original symlink-recording change
  used for `FileChangeKind`.
- [Skipping a `Linked` entry during recursive restore changes the previously-implicit behavior
  from "crash mid-restore" to "silently incomplete unless the user reads the skipped count"] →
  Mitigated by making the skipped count a first-class part of the reported outcome (not just a
  log line), consistent with how `BackupExecutor` already surfaces failed-path counts.
- [Reusing `RemoveFromMirror` from the `Linked` branch means every backup run with a file-to-link
  transition now does one extra mirror-path existence check/delete attempt] → Negligible cost;
  this is a metadata-only operation on the same class of path the `Delete` branch already handles
  identically.
