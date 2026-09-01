## MODIFIED Requirements

### Requirement: List snapshots
The system SHALL provide a command that lists the recorded snapshots for a profile, including each snapshot's timestamp and summary statistics, presented as an aligned table with column headers so each field lines up across rows.

#### Scenario: Listing snapshot history
- **WHEN** a user requests the snapshot history for a profile that has completed at least one backup run
- **THEN** the system displays each recorded snapshot's timestamp and summary statistics, such as files changed and bytes transferred, as a row of an aligned table

#### Scenario: No snapshots recorded
- **WHEN** a user requests the snapshot history for a profile that has not yet completed any backup run
- **THEN** the system reports that no snapshots are recorded, rather than displaying an empty table

### Requirement: File version history
The system SHALL provide a command that lists the recorded versions of a specific file within a profile, including the timestamp at which each version was introduced and, if applicable, when it was superseded or deleted, presented as an aligned table with column headers so each field lines up across rows.

#### Scenario: Viewing a file's history
- **WHEN** a user requests the version history of a file path within a profile
- **THEN** the system displays each recorded version of that file with its timestamp, most recent first, as a row of an aligned table
