# snapshot-history Specification

## Purpose

Lets a user see what backups have been taken over time and recover a file's content as it existed at a specific point in the past, independent of what the live mirror currently contains.

## Requirements

### Requirement: List snapshots
The system SHALL provide a command that lists the recorded snapshots for a profile, including each snapshot's timestamp and summary statistics.

#### Scenario: Listing snapshot history
- **WHEN** a user requests the snapshot history for a profile that has completed at least one backup run
- **THEN** the system displays each recorded snapshot's timestamp and summary statistics, such as files changed and bytes transferred

### Requirement: File version history
The system SHALL provide a command that lists the recorded versions of a specific file within a profile, including the timestamp at which each version was introduced and, if applicable, when it was superseded or deleted.

#### Scenario: Viewing a file's history
- **WHEN** a user requests the version history of a file path within a profile
- **THEN** the system displays each recorded version of that file with its timestamp, most recent first

### Requirement: Restore a file at a given version or date
The system SHALL provide a command that extracts a specific file's content as it existed at a given version or as of a given date, without modifying the current live mirror.

#### Scenario: Restoring a previous version
- **WHEN** a user requests a file's content as of a date prior to its most recent change
- **THEN** the system writes that file's content, as it existed at that date, to a location specified by the user, and leaves the live mirror unchanged

### Requirement: Restore of a deleted file
WHEN a file existed in a previous snapshot but no longer exists in the current mirror, the system SHALL still allow its last known content to be restored.

#### Scenario: Restoring a deleted file
- **WHEN** a user requests a file that was deleted from the source in a later snapshot than the one requested
- **THEN** the system restores the file's content as it existed in the requested snapshot

### Requirement: Unknown path or version reported clearly
WHEN a user requests history or restoration for a path never tracked, or a version or date that does not exist, the system SHALL report a clear error rather than partial or misleading results.

#### Scenario: Path never backed up
- **WHEN** a user requests version history for a path that was never part of any recorded snapshot
- **THEN** the system reports that no history exists for that path
