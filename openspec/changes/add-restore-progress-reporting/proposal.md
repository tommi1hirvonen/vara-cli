## Why

The restore command currently extracts historical file content with no progress feedback at all - for a large file, the command appears to hang until the copy completes. The file's total size is already known upfront (`FileVersionRecord.Size`), so a byte-based progress bar is straightforward to add and will only become more valuable if directory restores are supported later.

## What Changes

- Add a byte-based progress bar to the restore command, shown while a historical file's content is being extracted to its destination.
- Add an incremental-copy progress hook (`onBytesCopied`) to `IContentStore.ExtractTo`, mirroring the existing hooks on `StoreFromStream` and `PlaceAtMirrorPath`, so `ExtractTo` can report progress as it copies rather than only on completion.
- Since the total size is known before the copy starts, no scan/indeterminate phase is needed - the bar starts immediately at 0% with a known total.
- Non-interactive/redirected output falls back to plain, non-progress-bar output, consistent with the backup command's existing fallback behavior.

## Capabilities

### Modified Capabilities
- `progress-reporting`: the "Incremental progress during in-flight file transfers" requirement extends to the restore command's single-file extraction, not just backup's store/mirror-placement copies.
- `snapshot-history`: the restore requirements gain a progress-reporting expectation for large files.

## Impact

- `Vara.Core/Abstractions/IContentStore.cs`: `ExtractTo` signature gains an optional `onBytesCopied` callback.
- `Vara.Infrastructure/Storage/FileSystemContentStore.cs`: `ExtractTo` implementation reports incremental progress during its copy.
- `Vara.Application/History/SnapshotHistoryService.cs`: `RestoreAsOf`/`RestoreVersion` accept and forward a progress callback.
- `Vara.Cli/Commands/RestoreCommand.cs`: wires up a live progress bar (interactive) or plain periodic output (non-interactive), reusing existing presentation building blocks (`BackupProgressCalculator`-equivalent math, `ProgressDisplayGate`-equivalent rate limiting) where they fit a single-file transfer.
- Test files for each of the above.
