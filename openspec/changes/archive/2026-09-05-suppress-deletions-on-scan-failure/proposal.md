## Why

A scan failure that prevents a path from being enumerated is currently indistinguishable, downstream, from that path genuinely no longer existing. `BackupDiffer.Diff` only sees the set of paths the scanner *did* successfully enumerate; it never receives `ScanResult.Failures`. So any manifest-tracked path that isn't re-observed this run - whether because it was truly deleted, or because a source root is missing/typo'd, a subtree is permission-denied, or a file is transiently locked - is classified `Deleted` and the executor removes it from the mirror. An unplugged drive, a drive-letter change, or a config typo currently turns a single backup run into a silent mass deletion of the mirror, with zero reported failures. The same gap already affects the narrower, already-shipped `UnreadableDirectory`/`UnreadableEntry` cases, just with a smaller blast radius.

## What Changes

- Add `ScanFailureReason.SourceUnavailable` for a configured source whose path is neither an existing file nor an existing directory at scan time (currently silently skipped with no failure recorded).
- Extend `ScanFailure` with enough information to identify the affected path's location in mirror-path space (derived the same way `ScannedEntry.RelativePath` already is, via `AbsolutePathMirrorMapper`), so a failure can be matched against manifest-tracked paths regardless of which source produced them.
- `BackupDiffer.Diff` accepts the scan's failures and excludes any current-state path that is equal to, or nested under, a failed path's mirror-space location from `DeletedPaths` - for all three `ScanFailureReason` values (`SourceUnavailable`, `UnreadableDirectory`, `UnreadableEntry`), not just the new one.
- A path suppressed this way is left untouched in the mirror and manifest for this run (no manifest row is written for it) and is re-evaluated normally - as unchanged, changed, or genuinely deleted - on the next run once the source/directory/file becomes readable again.
- `BackupPipeline` wires the scanner's failures through to the differ.

## Capabilities

### New Capabilities

(none)

### Modified Capabilities

- `backup-execution`: a scan failure (missing source, unreadable subtree, or unreadable file) SHALL suppress deletion classification for the paths it affects, instead of the run silently treating them as deleted.

## Impact

- `Vara.Core.Abstractions.IFileSystemScanner`: `ScanFailure` record gains a field; `ScanFailureReason` gains a member.
- `Vara.Infrastructure.FileSystem.DirectoryFileSystemScanner`: reports `SourceUnavailable` for a missing source path; populates the new `ScanFailure` field at every failure site.
- `Vara.Application.Backup.BackupDiffer`: `Diff` signature changes to accept failures; `DeletedPaths` computation excludes paths under a failed root.
- `Vara.Application.Backup.BackupPipeline`: passes `scanResult.Failures` into `Diff`.
- Tests: `Vara.Infrastructure.Tests.FileSystem.DirectoryFileSystemScannerTests`, `Vara.Application.Tests.Backup.BackupDifferTests`, and any integration tests exercising missing/unreadable sources.
