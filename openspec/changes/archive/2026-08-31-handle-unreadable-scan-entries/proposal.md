## Why

`DirectoryFileSystemScanner` catches `IOException` inconsistently across its internal
try/catch sites, and its directory-enumeration failure handling drops errors silently.
An ACL-protected file or folder (e.g. `System Volume Information`, an admin-only
directory) throws `UnauthorizedAccessException` rather than `IOException` when its
attributes are read. That exception is not caught at the per-entry attribute-read
sites, so it propagates out of the scan, through `BackupPipeline.Run`, and past
`ErrorReporting`'s domain-exception whitelist, surfacing as a raw unhandled stack
trace that aborts the entire backup run - not just the affected path. Separately,
when a whole subdirectory cannot be enumerated (also due to permissions), the failure
is swallowed with no record anywhere, so that subtree silently vanishes from the run
with no indication in the snapshot summary that anything was skipped. Both behaviors
contradict the "Unreadable files do not abort the run" requirement already established
for the backup-execution capability, which today only covers per-file failures during
transfer, not failures encountered while scanning.

## What Changes

- Catch `UnauthorizedAccessException` alongside `IOException` at every scanner site
  that reads filesystem metadata for an individual entry (attribute reads in `Walk`'s
  enumeration loop, `ToEntry`, and `TryGetLinkTarget`), so an ACL-protected file or
  link no longer crashes the run.
- When a directory cannot be enumerated (permission denied or other I/O failure),
  record the failure instead of silently skipping it, so the run's result and
  snapshot summary reflect that a subtree was not backed up.
- Extend the existing "Unreadable files do not abort the run" requirement (or add an
  adjacent requirement) in the `backup-execution` spec to explicitly cover
  unreadable/inaccessible directories and entries encountered during scanning, not
  just files that fail during transfer.
- Add scanner test coverage for an ACL-protected directory (recorded as a scan
  failure, run continues). An ACL-protected single file was investigated but is
  not exercisable this way - see design.md's "Verified limitation" note; the
  existing per-file exception-handling widening remains as defensive coding.

## Capabilities

### New Capabilities
(none)

### Modified Capabilities
- `backup-execution`: broadens "Unreadable files do not abort the run" so that
  scan-time failures (unreadable directories and entries, not just locked files
  encountered during transfer) are also recorded rather than causing the run to
  abort or vanish silently.

## Impact

- `src/Vara.Infrastructure/FileSystem/DirectoryFileSystemScanner.cs`: exception
  handling changes at each per-entry metadata read; scan result type needs a way to
  surface a skipped/failed path (directory or entry) alongside successfully scanned
  entries.
- `src/Vara.Core/Abstractions` (scanner abstraction / `ScannedEntry`-adjacent types):
  likely needs a new shape to carry scan-time failures out of `IFileSystemScanner`.
- `src/Vara.Application/Backup/BackupPipeline.cs` and `BackupExecutor.cs`: need to
  consume scan-time failures and fold them into `SnapshotStats`/`BackupRunResult`
  alongside existing per-file `FailedPaths`.
- `tests/Vara.Infrastructure.Tests/FileSystem/DirectoryFileSystemScannerTests.cs`:
  new cases for ACL-protected files/directories.
- No change to `ErrorReporting.cs`'s whitelist is needed; the fix removes the
  uncaught-exception path rather than adding it to the whitelist.
