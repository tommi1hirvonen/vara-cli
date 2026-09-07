## ADDED Requirements

### Requirement: Dry-run mode reports planned changes without executing them
The system SHALL provide a `--dry-run` option to `vara backup` that performs the same scan, diff, and move-detection planning stages as a real run - so its reported counts reflect actual exclude/glob filtering and move detection - but performs no writes: no content is written to the mirror or content store, no manifest rows are recorded, and no new snapshot record is created. A dry run SHALL still acquire the profile's run lock for the duration of planning, refusing to start under the same conditions a real run would, so its output reflects a consistent view of the manifest and it cannot race a concurrently starting real run. The system SHALL report the planned added/changed/moved/deleted counts, the total bytes that would be transferred, and any paths that could not be scanned (and would therefore be skipped by a real run), instead of a completed-run summary.

#### Scenario: Dry run reports planned changes without writing anything
- **WHEN** a user runs `vara backup --dry-run` for a profile with pending changes
- **THEN** the system reports the counts of files that would be added, changed, moved, and deleted, and the total bytes that would be transferred, and afterward the mirror, content store, and manifest are unchanged - no new snapshot record exists

#### Scenario: Dry run refuses to start while a real run is in progress for the same target
- **WHEN** a user runs `vara backup --dry-run` for a profile while a real backup or prune run against the same target is already in progress
- **THEN** the system refuses to start the dry run, reporting the same shared-target-busy error a real run would report in the same situation

#### Scenario: Dry run reports unreadable paths without treating them as an error
- **WHEN** a user runs `vara backup --dry-run` for a profile where at least one source path cannot be scanned (for example, an unreadable subtree)
- **THEN** the system reports those paths as ones a real run would skip, without failing the dry run itself or requiring `--dry-run` to abort

#### Scenario: No changes pending
- **WHEN** a user runs `vara backup --dry-run` for a profile with no changes since the last run
- **THEN** the system reports zero planned changes and exits successfully
