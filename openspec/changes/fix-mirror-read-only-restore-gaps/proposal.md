## Why

The `protect-hardlinked-mirror-files` change made hardlinked mirror entries read-only, but two reachable gaps remain, both verified empirically against the current implementation: (1) re-placing the *same* content at a mirror path that is already a hardlink to that same blob leaves the mirror file **and** its blob permanently writable, silently losing the protection the change exists to provide; and (2) relocating a mirror entry onto an existing read-only destination now throws `UnauthorizedAccessException`, turning a previously-working crash-recovery move into a permanently failing one. Both are triggered by the ordinary "interrupted run" recovery paths the system already handles elsewhere, so they are not hypothetical.

## What Changes

- `PlaceAtMirrorPath` re-asserts read-only on the final mirror path after the atomic rename whenever the placement was made via a hardlink, rather than relying solely on the caller-supplied `previousContentHash` to restore protection. This makes the invariant self-healing: protection no longer depends on whether the caller happened to know the superseded content's hash.
  - Root cause: when the existing mirror entry is a hardlink to the *same* blob as the incoming content, clearing the destination's read-only attribute before the overwrite also clears it on the shared underlying file - which is simultaneously the staged file and the blob. The staged file is then moved into place writable, and with `previousContentHash` null (every `Add` operation) nothing restores it.
- `MoveMirrorEntry` clears a pre-existing destination's read-only attribute before its `File.Move(..., overwrite: true)`, and re-asserts read-only on the destination afterward when the relocated entry was itself read-only, so a move onto an occupied mirror path keeps working exactly as it did before mirror entries became read-only.
  - Root cause: the archived change's design assumed `MoveMirrorEntry` always targets a not-yet-existing path, but the method passes `overwrite: true` deliberately and its own documentation describes an interrupted-run scenario in which the destination can already exist.
- No signature changes, no change to hardlink-based deduplication, and no change to which placements are read-only versus writable - only to when that attribute is correctly (re-)established.

## Capabilities

### New Capabilities

(none)

### Modified Capabilities
- `backup-execution`: strengthens the existing "Hardlinked mirror entries are read-only" requirement so protection holds when a mirror path is re-placed with content it already holds, and so relocating a read-only mirror entry onto an existing mirror path still succeeds.

## Impact

- `src/Vara.Infrastructure/Storage/FileSystemContentStore.cs` (`PlaceAtMirrorPath`, `MoveMirrorEntry`): re-assert read-only on the placed mirror path for hardlink placements; clear-and-restore around `MoveMirrorEntry`'s overwrite.
- `tests/Vara.Infrastructure.Tests/Storage/FileSystemContentStoreTests.cs`: regression coverage for re-placing identical content over an existing hardlinked entry with no `previousContentHash`, and for moving a read-only entry onto an existing read-only destination.
- No public API, interface, or manifest-schema changes; `IContentStore`, `BackupPlanner`, and `BackupExecutor` are untouched.
- No behavior change for the already-correct paths (`Change` with a known previous hash, copy-fallback placements, `RemoveFromMirror`, `DeleteContent`, `TryDelete`).
