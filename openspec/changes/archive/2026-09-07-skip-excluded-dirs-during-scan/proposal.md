## Why
`DirectoryFileSystemScanner` recurses into every subdirectory unconditionally and only checks a
source's exclude list against each entry afterward, in `ScanSource`. An excluded directory (for
example `.git`, `node_modules`, or `bin`) is therefore still fully walked: its contents are
enumerated, `File.GetAttributes` is called on every entry, and if any entry underneath it is
unreadable (an ACL-protected file, say), that failure is still recorded as a `ScanFailure` even
though the user explicitly excluded that directory and does not care about its contents. This
wastes I/O on every scan and can surface spurious permission-failure noise for directories the
user intentionally opted out of backing up.

## What Changes
- Check a source's literal exclude list against a directory's own relative path *before*
  recursing into it during traversal, so an excluded directory is never walked, never has its
  entries enumerated, and never contributes a `ScanFailure` for content the user excluded.
- Leave the existing per-entry exclude/glob filtering in `ScanSource` in place for files (and for
  any directory reached that isn't itself excluded), since glob-based directory-level pruning is
  out of scope for this change (see design.md).

## Capabilities

### Modified Capabilities
- `backup-execution`: strengthens "Source exclusion rules honored" so a literally-excluded
  directory is never traversed, and unreadable content within it is never scanned or reported as
  a failure.

## Impact
- `Vara.Infrastructure.FileSystem.DirectoryFileSystemScanner` (`Walk`/`ScanSource`).
- Scan performance improves for profiles with large excluded subtrees (for example a `node_modules`
  or build-output directory).
- Scan failure reports for excluded directories disappear (previously spurious).
- No change to which files ultimately appear in the mirror - only when/whether their exclusion is
  detected during traversal versus after.
