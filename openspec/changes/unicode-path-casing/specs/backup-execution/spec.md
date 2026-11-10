## MODIFIED Requirements

### Requirement: Incremental change detection
The system SHALL determine which files changed since the last successful snapshot by comparing current source file size and modification time against the recorded manifest, without reading or transferring unchanged file content. The system SHALL match a scanned source path against the manifest's recorded paths case-insensitively, consistent with the case-insensitive, case-preserving semantics of the filesystems it targets, so that a path differing from a recorded path only in character casing is recognized as the same tracked file rather than an unrelated addition. This case-insensitivity SHALL apply to the letters of every script, including accented and other non-ASCII letters, and not only to the unaccented Latin letters A to Z. When the manifest's underlying storage holds multiple historical rows for the same logical path recorded under different casings, resolving that path's current state SHALL deterministically select exactly one of them (the most recently recorded), never nondeterministically vary between runs. A deletion recorded for a path under any casing SHALL apply to the logical path, so that no older row recorded under another casing keeps it tracked.

#### Scenario: No changes since last run
- **WHEN** a backup run is started and no source file has changed since the last successful snapshot
- **THEN** the run completes without copying any file content and records a new snapshot reflecting no changes

#### Scenario: File's casing changes without content changing
- **WHEN** a source file's name changes only in character casing (for example, `Photo.JPG` renamed to `photo.jpg`) while its size and modification time remain otherwise consistent with the recorded manifest entry
- **THEN** the file is matched to its existing manifest entry rather than classified as a new addition, its content is not re-transferred, and the prior entry is not left as an orphaned, unreferenced record

#### Scenario: Multiple historically recorded casings of the same path
- **WHEN** the manifest's underlying storage holds more than one historical row for what is, case-insensitively, the same logical path (for example, from casing changes recorded before this capability existed)
- **THEN** resolving that path's current state consistently selects the same, most-recently-recorded row every time, regardless of query evaluation order, rather than nondeterministically alternating between runs

#### Scenario: File with a non-ASCII name changes casing and content
- **WHEN** a source file whose name contains an accented letter is renamed only in the casing of that letter (for example `äiti.txt` renamed to `Äiti.txt`, or the reverse) and is then modified, and two backup runs follow
- **THEN** the first run records one change for the file, the second run records no change and copies no content, and the manifest's current state holds exactly one entry for the file, spelled as the latest row spells it

#### Scenario: Several recorded casings of a non-ASCII path
- **WHEN** the manifest holds rows for the same logical path under two casings that differ only in a non-ASCII letter, such as `Äiti.txt` and `äiti.txt`
- **THEN** the current state holds exactly one entry for it, taken from the most recently recorded row whichever casing that row used, and the answer is the same on every run

#### Scenario: Deletion recorded under another casing of a non-ASCII path
- **WHEN** the manifest holds a live row for `Äiti.txt`, a later live row for `äiti.txt`, and a still later deletion recorded for `äiti.txt`
- **THEN** the path is not part of the current state, and the next backup run does not record it as deleted again
