## Why

A profile source (or target) configured as a bare drive root, e.g. `C:\`, is silently backed up
from the wrong location. `DirectoryFileSystemScanner` normalizes the configured path by trimming
trailing separators (`TrimEnd('\\', '/')`), which turns `C:\` into `C:` - a Windows
drive-relative path meaning "the current directory on drive C", not the drive root.
`Directory.Exists("C:\")` (checked before the trim) passes, so no error is ever raised; the scan
then silently walks whatever the process's per-drive current directory happens to be instead of
the real drive root. This was confirmed by reproducing the exact API behavior directly
(`Directory.EnumerateFileSystemEntries("C:")` returns entries from the current directory, not the
drive root, even immediately after `cd C:\`).

Separately, no validation anywhere currently requires a profile's source or target paths to be
absolute/rooted at all. A relative path is just as nonsensical for a backup source or target as a
bare drive letter, and today it would be silently accepted and resolved against whatever the
process's working directory happens to be at scan time - a related silent-wrong-tree risk that
should be rejected outright rather than left to resolve unpredictably.

## What Changes

- Fix path-root normalization so a drive root (e.g. `C:\`) is never turned into its drive-relative
  form (e.g. `C:`) before being handed to filesystem enumeration APIs. Use a root-safe trim
  (`Path.TrimEndingDirectorySeparator`, which leaves a root like `C:\` unchanged) in place of the
  raw `TrimEnd('\\', '/')` used for this purpose in `DirectoryFileSystemScanner`.
- **BREAKING**: Add validation that rejects a profile's target root, or any of its source paths,
  if the path is not a fully-qualified, rooted, absolute path. A profile that currently configures
  a relative source or target path will now fail to load with a clear validation error instead of
  silently resolving against the process's working directory at scan/backup time.
- Pin down in the backup-execution spec that a drive-letter-root source path is scanned from the
  actual drive root, not from any notion of a "current directory", closing the gap that let this
  defect go unspecified.

## Capabilities

### New Capabilities
(none)

### Modified Capabilities
- `profile-config`: adds a requirement that a profile's target root and every source path must be
  absolute/rooted, rejecting relative paths at validation time.
- `backup-execution`: adds a scenario to the existing "One-to-one mirror of source state"
  requirement clarifying that a drive-letter-root source (e.g. `C:\`) is scanned from the real
  drive root.

## Impact

- `Vara.Core.Configuration.Profile` / `Source` construction - new rooted-path validation.
- `Vara.Infrastructure.FileSystem.DirectoryFileSystemScanner` - root-safe trailing-separator trim.
- Any profile-loading error surface (CLI output) that reports validation failures - gains a new
  error case for non-rooted paths.
- Existing tests asserting today's permissive (relative-path-accepting) behavior in
  `Vara.Core.Tests` / `Vara.Infrastructure.Tests` will need updating.
