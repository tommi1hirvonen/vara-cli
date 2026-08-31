## Why

`FileSystemContentStore.PlaceAtMirrorPath` decides hardlink-vs-copy once per store instance (cached from a volume-level probe) and never revisits that decision per call. When a single blob is already referenced by 1024 mirror paths - NTFS's per-file hard-link cap - `CreateHardLink` fails for every further placement of that blob. `BackupExecutor` catches the resulting `IOException`, records the file as failed, and skips writing a manifest entry for it. Because no manifest entry is written, the next run re-plans the same file as needing to be placed again, hits the same cap, and fails again - permanently, every run, with no recovery path. This mostly affects profiles with many byte-identical files (e.g. duplicate empty files, marker files, or config templates repeated across a large tree).

## What Changes

- `FileSystemContentStore.PlaceAtMirrorPath` falls back to a real file copy for an individual placement when hardlink creation fails, instead of only choosing hardlink-vs-copy once per store instance.
- A blob that has exhausted its hard-link capacity no longer causes permanent per-run failures for the affected mirror paths; those paths are placed via copy instead and the run's manifest is written normally.
- `IContentStore.PlaceAtMirrorPath`'s contract is clarified to document the per-placement fallback in addition to the existing per-volume fallback.

## Capabilities

### New Capabilities
(none)

### Modified Capabilities
- `backup-execution`: adds graceful degradation when an individual blob's hard-link count is exhausted on an otherwise hardlink-capable target, so the affected mirror placement falls back to a real copy instead of being reported as a failed file.

## Impact

- `src/Vara.Infrastructure/Storage/FileSystemContentStore.cs` (`PlaceAtMirrorPath`)
- `src/Vara.Core/Abstractions/IContentStore.cs` (doc comment only, no signature change)
- `tests/Vara.Infrastructure.Tests/Storage/FileSystemContentStoreTests.cs` (new coverage for the per-blob fallback path)
- No public API, storage layout, or manifest schema changes.
