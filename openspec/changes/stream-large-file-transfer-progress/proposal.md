## Why

During a backup run, progress is only reported once a file's transfer fully completes, not while it is in flight. For a run dominated by a few very large files, this makes the displayed bytes-transferred, percentage, throughput, and ETA freeze for the entire duration of each file's transfer and then jump discontinuously when it finishes, giving the user no sense of whether the run is progressing or hung, and no meaningful throughput/ETA during exactly the situation (large files) where that information matters most.

## What Changes

- Report transfer progress incrementally as bytes are actually copied within a single file's transfer, not only once per completed file, so the displayed percentage, throughput, and ETA advance continuously during a large file's transfer instead of jumping in file-sized steps.
- Collapse the content store's current two-pass write (copy source → temp file, then re-read the temp file to compute its hash) into a single streamed pass that copies and hashes simultaneously, using the same pass to drive incremental progress. This removes a full redundant read of every transferred file's content as a side effect.
- Apply the same incremental-progress treatment to the mirror-placement fallback copy (`PlaceAtMirrorPath`'s `File.Copy`, used when the target filesystem doesn't support hardlinks or a blob has hit its hard-link limit), which today is an equally invisible whole-file copy.
- Make the run's cumulative bytes-transferred counter safe to update from multiple concurrently transferring files' chunk-level progress callbacks (today it is only ever updated once per file, serialized by an existing per-file lock; incremental reporting removes that incidental safety).

## Capabilities

### New Capabilities

(none)

### Modified Capabilities

- `progress-reporting`: progress during an in-flight file transfer must be reported incrementally as content is copied, rather than only upon that file's completion.

## Impact

- `Vara.Core.Abstractions.IContentStore.StoreFromStream`: gains an optional chunk-progress callback parameter (additive, non-breaking).
- `Vara.Infrastructure.Storage.FileSystemContentStore`: `StoreFromStream` reworked to a single streamed copy+hash pass; `PlaceAtMirrorPath`'s fallback `File.Copy` calls replaced with a progress-reporting streamed copy.
- `Vara.Application.Backup.BackupExecutor`: forwards chunk-level progress from the content store up through its existing `onBytesTransferred` callback instead of invoking it once per file.
- `Vara.Application.Backup.BackupPipeline`: its running `bytesSoFar` total must become safe under concurrent chunk-level updates (e.g. via `Interlocked`), independent of the manifest-write lock.
- No change to `Vara.Cli.Commands.BackupCommand`, `ProgressDisplayGate`, or `BackupProgressCalculator` - the existing rate-limited redraw and monotonic-value guard already handle a higher-frequency, potentially out-of-order stream of progress reports correctly.
