## Context

See `proposal.md` - Why for the motivation (source-relative mirror paths collide across sources).

Today's mapping lives entirely in `DirectoryFileSystemScanner`:
- `root = source.Path.TrimEnd('\\', '/')`, then `Walk(root, ...)` enumerates entries under `root`.
- `relativePath = Path.GetRelativePath(root, path)` - relative to the *source's own root*, discarding the source root segment entirely.
- That same `relativePath` is used for: (a) exclude/glob matching (`IsExcluded`, `MatchesGlob`), and (b) the `ScannedEntry.RelativePath` that flows unmodified through `BackupDiffer`, `BackupPlanner`, `BackupExecutor`, `IContentStore` mirror placement, and `ISnapshotRepository` manifest keys.
- Single-file sources take a separate branch that uses `Path.GetFileName(source.Path)` as the relative path.

Because excludes/globs are authored relative to a source (e.g. `subfolder1\`), that matching must keep operating on a source-relative path. The only thing that needs to change is what path is handed onward as the entry's mirror/manifest identity.

## Goals / Non-Goals

**Goals:**
- Derive each `ScannedEntry`'s mirror/manifest path from its full absolute source path, with the drive letter's colon stripped, for both directory and single-file sources.
- Keep exclude-list and glob matching working exactly as today, evaluated against a path relative to the entry's own source.
- Make the two colliding-source scenarios (shared subfolder name, shared top-level file name) structurally impossible rather than merely discouraged.

**Non-Goals:**
- No migration of existing mirrors/manifests (pre-release, greenfield).
- No change to `history`/`restore` CLI ergonomics for the now-longer path argument.
- No support for UNC (`\\server\share\...`) source paths.
- No change to move/rename detection, hashing, dedup, or concurrency behavior - they continue to treat the mirror/manifest path as an opaque string key.

## Decisions

**Decision: Compute two paths per entry inside the scanner - a source-relative path for filtering, and an absolute-path-derived path for the entry's public `RelativePath`.**
`Walk`/`IsExcluded`/`MatchesGlob` continue to receive `Path.GetRelativePath(root, path)` exactly as today - this preserves all existing exclude/glob test coverage and config semantics untouched. Only the value passed into `ToEntry(...)` (and therefore `ScannedEntry.RelativePath`) changes, to a value derived from the entry's full absolute path (`path`, not `relativePath`).
Alternative considered: reuse `relativePath` and prefix it with a transformed `source.Path`. Rejected as equivalent in result but more error-prone (would need to re-derive the drive-letter transform per source instead of once per absolute path, and risks subtle double-separator bugs at the join point) versus simply transforming the full absolute path directly.

**Decision: Drive-letter transform is "strip the colon", applied once via a small helper.**
`C:\Users\john\...` -> `C\Users\john\...`. No other transform (no case normalization of the drive letter, no reordering) - matches the user's confirmed preference and keeps the mapping trivially reversible for diagnostics/debugging.
Alternative considered: nest under a fixed `drives\` folder or lowercase the letter. Rejected - adds a layer of indirection with no behavioral benefit, and the user confirmed a plain colon-strip is sufficient.

**Decision: Single-file sources go through the same absolute-path transform as directory-source entries, not a bare filename.**
Today a single-file source uses `Path.GetFileName(source.Path)`, which has the same collision risk as directory sources. Since the transform is applied to the *entry's* absolute path either way, unifying the two branches removes a special case rather than adding one.

**Decision: No path-length mitigation is implemented in this change.**
Deeply-nested sources (e.g. under `AppData`) combined with the target root and the extra drive-letter segment increase absolute mirror path length, which could approach Windows' legacy 260-character limit on a system without long-path support enabled. This is called out as a known risk (see below) rather than mitigated here (e.g. by enabling `LongPathsEnabled` app-wide), since it doesn't change any spec-level behavior and can be addressed independently if it proves to matter in practice.

## Risks / Trade-offs

- **[Risk]** Longer mirror paths increase the chance of hitting Windows' legacy `MAX_PATH` (260 char) limit on systems without long-path support enabled -> Mitigation: none in this change; flagged as a follow-up if it surfaces in practice (e.g. enabling long-path support app-wide).
- **[Risk]** `history`/`restore` CLI arguments become longer and less convenient to type by hand -> Mitigation: explicitly deferred; not addressed by this change.
- **[Trade-off]** Existing scanner/backup tests that assert today's source-relative `RelativePath` values (e.g. `"a.txt"`, `Path.Combine("sub", "b.txt")`) will need updating to assert the new absolute-path-derived values - this is expected test churn from the breaking change, not a regression.

## Migration Plan

None. This is a pre-release application with no deployed mirrors or manifests to migrate; the new mapping simply becomes how every backup run (including the first one for any profile) lays out its target directory from this point forward.
