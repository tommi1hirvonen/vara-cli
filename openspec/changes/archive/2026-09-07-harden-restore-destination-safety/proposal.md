## Why
Two related gaps let a restore silently do the wrong thing to files on disk:

1. `FileSystemContentStore.ExtractTo` deletes an existing destination file before copying the
   restored content into place. If the copy is interrupted (disk full, process killed, permission
   error), the destination is left empty or missing rather than either fully restored or
   untouched - the original content is lost. Mirror writes already avoid this exact problem via a
   stage-then-atomically-rename pattern (`PlaceAtMirrorPath`); restore writes do not.
2. The guard that refuses to restore into the live mirror (`IsWithinMirror`, used by
   `SnapshotHistoryService.GuardDestination`) - and its documented duplicate in
   `SnapshotPathResolver` (used to interpret a user-typed path for `history`/`restore`/`show`/
   `diff`) - both compare paths using only `Path.GetFullPath`, which normalizes a path string but
   never resolves reparse points. A junction or symlink whose target lands inside the mirror lets
   a restore destination that lexically looks like it's outside the mirror actually land inside
   it, bypassing the guard that exists specifically to prevent a restore from corrupting the
   backup that would be needed to recover from it.

Both gaps sit in the same restore code path (`GuardDestination` → `ExtractTo`) and the same file
(`FileSystemContentStore`), so they're addressed together.

## What Changes
- Make `ExtractTo` write via the same stage-in-temp-then-atomically-rename pattern
  `PlaceAtMirrorPath` already uses, so a restore is either fully written or leaves the prior
  destination content untouched - never a partially-written or deleted-but-not-replaced file.
- Make the mirror-containment check (`FileSystemContentStore`'s internal `IsPathWithinRoot`, used
  by both `IsWithinMirror` and `ResolveMirrorPath`) resolve each path to its real, final location -
  following any reparse points along the way - before comparing it against the mirror root, so a
  junction or symlink that ultimately resolves inside the mirror is correctly detected as "inside
  the mirror" regardless of what the un-resolved path string looks like.
- Eliminate `SnapshotPathResolver`'s separate, lexical-only duplicate of the mirror-containment
  check. It will instead receive the caller's already-available `IContentStore.IsWithinMirror`
  (now reparse-aware) as a parameter, so there is exactly one implementation of "is this path
  inside the mirror" and both use sites automatically stay consistent.

## Capabilities

### Modified Capabilities
- `snapshot-history`: strengthens "Restore never writes into the live mirror" to require
  reparse-point-aware containment checking, and adds a requirement that restore writes are atomic
  (never leave a destination partially written or deleted-without-replacement on failure).

## Impact
- `Vara.Infrastructure.Storage.FileSystemContentStore` (`ExtractTo`, `IsPathWithinRoot`).
- `Vara.Infrastructure.Interop.Kernel32` gains a `GetFinalPathNameByHandle` P/Invoke (same
  source-generated `LibraryImport` pattern already used for `CreateHardLink`).
- `Vara.Application.History.SnapshotPathResolver.TryResolve`'s signature changes to accept an
  `isWithinMirror` delegate instead of computing its own containment check; every call site
  (`ShowCommand`, `HistoryCommand`, `DiffCommand`, `RestoreCommand`, `DirectoryArgumentResolver`)
  is updated to pass `services.ContentStore.IsWithinMirror`.
- A restore interrupted mid-write now leaves the previous destination file intact instead of
  destroyed.
- A restore destination reached only through a junction/symlink into the mirror is now refused,
  matching the existing guard's intent.
