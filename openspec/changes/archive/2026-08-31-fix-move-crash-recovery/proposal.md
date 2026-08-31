## Why

`BackupExecutor.ExecuteMoveOrDelete` relocates a mirror entry via `IContentStore.MoveMirrorEntry` (an unconditional `File.Move` of the *old* mirror path onto the *new* one) before writing the corresponding `Deleted`/`Moved` manifest rows inside the snapshot's batched transaction. If the process is interrupted after the physical rename but before that transaction commits, the manifest reverts to its pre-run state on restart (old path still "current"), while the mirror file already lives at the new path. The next run's move-detection (matched by content hash) re-plans the same move and calls `MoveMirrorEntry` again — but the old mirror path no longer exists, so the rename throws every time. The per-operation failure handler catches this and reports the path as failed instead of crashing the run, but the manifest is never corrected, so the path fails on every subsequent run forever. This violates the `backup-execution` spec's existing guarantee that an interrupted batch's affected paths are "re-detected and re-recorded on the next run" without error, and it breaks the "version history carries forward" guarantee for moves specifically.

## What Changes

- Make mirror-side move recovery idempotent: when a planned move's source no longer exists in the mirror but the destination already holds the expected content (already relocated by an earlier interrupted run), treat the relocation as already satisfied instead of failing.
- Extend the `backup-execution` spec's crash/interruption-safety requirements to explicitly cover the move-specific recovery case, so a crash between the mirror rename and the manifest commit is guaranteed to self-heal on the next run rather than permanently sticking the affected path in the failed-paths list.
- No change to the happy-path move behavior, manifest schema, or public CLI surface.

## Capabilities

### New Capabilities
(none)

### Modified Capabilities
- `backup-execution`: the "Crash and interruption safety" / "Manifest writes are batched per snapshot" requirements are extended so that a move interrupted between the mirror rename and the manifest commit is re-recorded without error on the next run, instead of permanently failing.

## Impact

- `src/Vara.Infrastructure/Storage/FileSystemContentStore.cs`: `MoveMirrorEntry` (or its caller) needs to recognize and recover from the "source missing, destination already correct" case left behind by an interrupted prior run.
- `src/Vara.Application/Backup/BackupExecutor.cs`: `ExecuteMoveOrDelete`'s Move branch may need to consult the mirror state to distinguish a genuine failure from an already-completed relocation before recording the outcome.
- No public API/CLI surface changes. Existing Move/Delete happy-path tests remain valid; new tests are needed for the interrupted-move-then-retry scenario.
