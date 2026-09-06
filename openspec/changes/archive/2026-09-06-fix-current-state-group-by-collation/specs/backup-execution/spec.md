## MODIFIED Requirements

### Requirement: Incremental change detection
The system SHALL determine which files changed since the last successful snapshot by comparing current source file size and modification time against the recorded manifest, without reading or transferring unchanged file content. The system SHALL match a scanned source path against the manifest's recorded paths case-insensitively, consistent with the case-insensitive, case-preserving semantics of the filesystems it targets, so that a path differing from a recorded path only in character casing is recognized as the same tracked file rather than an unrelated addition. When the manifest's underlying storage holds multiple historical rows for the same logical path recorded under different casings, resolving that path's current state SHALL deterministically select exactly one of them (the most recently recorded), never nondeterministically vary between runs.

#### Scenario: No changes since last run
- **WHEN** a backup run is started and no source file has changed since the last successful snapshot
- **THEN** the run completes without copying any file content and records a new snapshot reflecting no changes

#### Scenario: File's casing changes without content changing
- **WHEN** a source file's name changes only in character casing (for example, `Photo.JPG` renamed to `photo.jpg`) while its size and modification time remain otherwise consistent with the recorded manifest entry
- **THEN** the file is matched to its existing manifest entry rather than classified as a new addition, its content is not re-transferred, and the prior entry is not left as an orphaned, unreferenced record

#### Scenario: Multiple historically recorded casings of the same path
- **WHEN** the manifest's underlying storage holds more than one historical row for what is, case-insensitively, the same logical path (for example, from casing changes recorded before this capability existed)
- **THEN** resolving that path's current state consistently selects the same, most-recently-recorded row every time, regardless of query evaluation order, rather than nondeterministically alternating between runs
