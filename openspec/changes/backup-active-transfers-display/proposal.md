## Why

During a backup run the live progress display shows throughput, ETA, and file counts, but gives no indication of *which* files are currently being transferred. A user watching a long run has no way to tell whether the backup is stuck on one large file or steadily working through many, which makes the display harder to trust and harder to use as a diagnostic signal.

## What Changes

- `BackupExecutor` gains `onFileStarted(string absolutePath)` and `onFileFinished(string absolutePath)` callbacks, fired when a transfer operation begins and when it exits, including failure.
- `BackupPipeline` maintains a thread-safe set of in-flight absolute paths and snapshots it into every `BackupProgress` report.
- `BackupProgress` gains an `ActivePaths` field carrying the snapshot.
- `BackupProgressState` (the Spectre task-state struct) is extended to carry `ActivePaths` through to the renderer.
- `BackupProgressColumn.RenderStats` renders up to three in-flight paths below the existing statistics rows, each truncated to a 48-character budget using the existing `PathLabelTruncator`. When more than three paths are active, a `+ N more` line is shown in place of the excess entries.

## Capabilities

### New Capabilities

*(none)*

### Modified Capabilities

- `progress-reporting`: New requirement — the backup progress display SHALL list currently-transferring file paths below the existing statistics rows.

## Impact

- `Vara.Application` — `BackupModels.cs` (`BackupProgress`), `BackupExecutor.cs`, `BackupPipeline.cs`
- `Vara.Cli` — `BackupProgressColumn.cs`, `BackupCommand.cs` (wiring), `BackupProgressState` (struct in `BackupProgressColumn.cs`)
- `PathLabelTruncator` reused as-is; no changes needed.
- No breaking changes to existing public APIs. The new `onFileStarted` callback parameter is optional (nullable `Action<string>?`), consistent with the existing optional callbacks on `Execute`.
