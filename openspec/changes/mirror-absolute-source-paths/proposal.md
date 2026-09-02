## Why

Today the scanner discards each source's own root when computing an entry's mirror path (`Path.GetRelativePath(source.Path, entryPath)`), so a source's contents land directly under the target root with no namespacing by source at all. Two sources that happen to share a subfolder or file name at the same depth (for example, two sources each containing a `src\` folder, or a top-level `notes.txt`) silently collide and overwrite each other in the mirror. Mapping each source's full absolute path into the mirror instead makes every mirror location unique by construction, eliminating this class of collision, since no migration is needed while the application is still pre-release.

## What Changes

- **BREAKING**: The target mirror's directory layout changes from a source-root-relative structure to one based on each entry's full absolute source path, with the drive letter's colon stripped (e.g. `C:\Users\john\Programming\src\main.py` mirrors to `<target>\C\Users\john\Programming\src\main.py`).
- Directory sources no longer have their contents merged into the target root; every source (and every single-file source) is mapped through its full absolute path, so distinct sources can never produce the same mirror path.
- Exclude and include/exclude glob matching continue to be evaluated against each source's own source-relative path (unchanged, internal-only behavior) - only the resulting mirror/manifest path identity changes.
- No migration path for existing mirrors or manifests is provided; this is a pre-release, greenfield change.

## Capabilities

### New Capabilities
(none)

### Modified Capabilities
- `backup-execution`: The "One-to-one mirror of source state" requirement changes from "matching relative paths and names" (relative to each source's own root) to a mirror path derived from each entry's full absolute source path, so files/directories from different sources can never collide in the mirror.

## Impact

- `Vara.Infrastructure.FileSystem.DirectoryFileSystemScanner`: continues computing a source-relative path for exclude/glob evaluation, but must derive the `ScannedEntry`'s mirror/manifest path from the entry's full absolute path with the drive letter's colon stripped, for both directory and single-file sources.
- `Vara.Core.Abstractions.ScannedEntry` (and any other model carrying `RelativePath`) is unaffected in shape, but the values it carries change meaning/format.
- Downstream consumers of `ScannedEntry.RelativePath` (`BackupDiffer`, `BackupPlanner`, `BackupExecutor`, `IContentStore` mirror operations, `ISnapshotRepository`) are unaffected in behavior since they treat the value as an opaque identifier/path, but existing tests asserting today's source-relative path format need updating.
- CLI commands that accept a mirror-relative path (`history`, `restore`) are unaffected in implementation, but users must now type the longer, absolute-style path; improving that UX is explicitly out of scope for this change.
- UNC-path sources (`\\server\share\...`) are out of scope; only drive-letter paths are addressed.
