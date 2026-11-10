## Why

The specification requires a scanned path to match its recorded path case-insensitively, as the Windows file systems do, and requires exactly one recorded row to decide a path's current state. The manifest does neither for names with non-ASCII letters. Its path column uses SQLite's `NOCASE` collation, which folds only the 26 ASCII letters, so `Äiti.txt` and `äiti.txt` are two paths to SQL and one path to the .NET dictionary the rows are loaded into. After a case-only rename plus an edit, both rows are returned and whichever is enumerated last silently wins; the stale one can win, and the file is then seen as changed and copied again on every run. A deletion recorded under one casing does not hide the live row of the other. Names with `ä`, `ö`, `å`, `é` and the like are ordinary for this tool's users.

## What Changes

- The current state of the manifest is resolved by the highest row id per path compared with the file system's case folding (letters of every script, not only A-Z), so one row decides, a deletion applies to the path whichever casing it was recorded under, and the latest row's casing is the one kept.
- The requirement on incremental change detection states that case-insensitivity covers non-ASCII letters, with scenarios for a rename in each direction, for several recorded casings, and for a deletion.
- Snapshot expiry (prune) decides which record is a path's current one with the same resolution, so a path deleted under another casing no longer keeps an old snapshot and its content alive for ever.
- No schema, collation, index or file-format change: a target written by this build stays readable by the C# build and the other way round.

## Capabilities

### New Capabilities
(none)

### Modified Capabilities
- `backup-execution`: the "Incremental change detection" requirement's case-insensitive matching and its single-row resolution are stated for all letters, not only ASCII, with new scenarios.
- `retention-pruning`: the "Unreferenced content garbage collection" requirement says which record counts as current when snapshots are removed, with scenarios for a deletion under another casing and for several casings.

## Impact

- `src/Vara.Engine/Storage/SqliteManifest.fs` (`currentState`, `removeSnapshots`, and one private resolver they share)
- `tests/Vara.Engine.Tests/Storage/SqliteManifestTests.fs`, `tests/Vara.Engine.Tests/Runs/BackupRunTests.fs` (new scenario tests, in the existing `Incremental change detection` module where it already exists), `tests/Vara.Engine.Tests/Runs/MaintenanceEngineTests.fs` (prune scenarios)
- Reading cost: the resolver returns each path's latest row including deletions, so a manifest with many deleted paths reads more rows than before. Prune now also resolves every path once, which is proportional to the number of distinct paths ever recorded and acceptable for an occasional command.
- Not changed, and listed in design.md: lookups of one path's history, which stay ASCII-insensitive.
