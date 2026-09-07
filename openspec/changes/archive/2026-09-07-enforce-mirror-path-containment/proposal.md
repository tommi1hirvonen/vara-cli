## Why

`FileSystemContentStore`'s mirror-write methods (`PlaceAtMirrorPath`, `MoveMirrorEntry`,
`RemoveFromMirror`) combine `_mirrorRoot` with a caller-supplied relative path via plain
`Path.Combine`, with no check that the result stays inside the target root.
`AbsolutePathMirrorMapper.ToMirrorPath` only maps drive-letter paths (`C:\...` -> `C\...`); a UNC
source path (`\\srv\share\a.txt`) passes through unchanged, and `Path.Combine` returns a rooted
second argument verbatim, so the "mirror path" for a UNC source is the UNC path itself. Nothing
in `Profile`/`Source` validation rejects this today - `Path.IsPathFullyQualified` treats UNC paths
as fully qualified, exactly like a drive-letter path - so a profile with a UNC source loads and
backs up "successfully" while writing/hardlinking/marking-read-only the original source file
instead of a mirror copy, and leaving nothing under the mirror root for that source. The same
unchecked combine would let a corrupted relative path (e.g. containing `..\..\`) from any other
caller of these methods escape the mirror root the same way. This is a data-integrity and
principle-of-least-surprise problem: a "successful" run should never write outside its own target
root or mutate a source file it was only supposed to read.

We want to keep the door open to properly supporting network-share sources later (extending
`AbsolutePathMirrorMapper` with a real UNC-to-mirror-path mapping), so the fix here is a
containment guard at the mirror-write boundary itself rather than a blanket rejection of UNC
paths at profile-config validation time. That guard turns today's silent, corrupting failure mode
into a loud, per-file recorded failure using the run's existing "unreadable file" handling, and it
will keep working unchanged once real UNC mapping is added later (a correctly mapped UNC entry
will simply resolve inside the mirror root and pass the same check).

## What Changes

- Add a containment check in `FileSystemContentStore` that resolves a mirror-relative path against
  `_mirrorRoot` and refuses to proceed if the resolved path is not inside the target root, applied
  at every mirror-write entry point: `PlaceAtMirrorPath`, `MoveMirrorEntry` (both the from- and
  to-path), and `RemoveFromMirror`.
- The check throws an `IOException`-derived exception, so it is caught by `BackupExecutor`'s
  existing per-operation `catch (IOException or UnauthorizedAccessException)` handling and
  recorded as a failed path exactly like a locked file today - it does not abort the run and does
  not require any change to `BackupExecutor`.
- No change to `Profile`/`Source`/`YamlProfileConfigLoader` validation: UNC (and any other
  fully-qualified) source paths remain accepted at config-load time. Until
  `AbsolutePathMirrorMapper` gains real UNC support, every entry from a UNC source will now fail
  loudly per-file via the new guard (since its unmapped mirror path always resolves outside the
  target root) instead of silently disappearing from the mirror while corrupting the source file.
- No change to restore-side behavior; `SnapshotHistoryService`'s existing `IsWithinMirror` guard
  for restore destinations is unrelated and untouched.

## Capabilities

### New Capabilities
(none)

### Modified Capabilities
- `backup-execution`: adds a requirement that mirror writes (place, move, remove) are confined to
  the target root and are refused - as a recorded per-file failure, not a run-aborting error - when
  a mirror path would resolve outside it; clarifies that a source path with no defined mirror-path
  mapping (for example, a UNC path today) is treated as failing this check for every entry it
  produces, rather than silently mapping outside the target root.

## Impact

- `src/Vara.Infrastructure/Storage/FileSystemContentStore.cs`: `PlaceAtMirrorPath`,
  `MoveMirrorEntry`, `RemoveFromMirror`, plus a new shared containment-check helper (built on the
  same resolution logic as the existing `IsWithinMirror`).
- `src/Vara.Core/Abstractions/IContentStore.cs`: doc comments describing the new failure mode.
- A new `IOException`-derived exception type for a mirror-path containment violation.
- Tests: `tests/Vara.Infrastructure.Tests/Storage/FileSystemContentStoreTests.cs`,
  `tests/Vara.Application.Tests/Backup/BackupExecutorTests.cs`,
  `tests/Vara.Infrastructure.Tests/FileSystem/DirectoryFileSystemScannerTests.cs` (to add UNC
  source coverage if relevant).
- No changes to `Vara.Core/Configuration/Profile.cs`, `Source`, or
  `Vara.Infrastructure/Configuration/YamlProfileConfigLoader.cs`.
