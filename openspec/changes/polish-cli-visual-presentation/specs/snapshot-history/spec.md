## MODIFIED Requirements

### Requirement: List snapshots
The system SHALL provide a command that lists the recorded snapshots for a profile, including each snapshot's timestamp and summary statistics, presented as an aligned table with column headers so each field lines up across rows, with each row's status rendered in the severity style matching that snapshot's outcome (per the `cli-presentation` capability), and numeric fields right-aligned within their column.

#### Scenario: Listing snapshot history
- **WHEN** a user requests the snapshot history for a profile that has completed at least one backup run
- **THEN** the system displays each recorded snapshot's timestamp and summary statistics, such as files changed and bytes transferred, as a row of an aligned table

#### Scenario: No snapshots recorded
- **WHEN** a user requests the snapshot history for a profile that has not yet completed any backup run
- **THEN** the system reports that no snapshots are recorded, rather than displaying an empty table

#### Scenario: Snapshot status reflects outcome severity
- **WHEN** the listed snapshots include a mix of complete, failed, and in-progress runs
- **THEN** each row's status is rendered in the severity style corresponding to that snapshot's outcome (for example, a failed run's status in the hard-error style), so outcomes are distinguishable at a glance without reading the status text

#### Scenario: Numeric statistics are right-aligned
- **WHEN** the snapshot listing includes statistics of varying digit counts (for example, file counts and byte totals)
- **THEN** each numeric column's values are right-aligned within their column, so the digits line up regardless of value length

### Requirement: File version history
The system SHALL provide a command that lists the recorded versions of a specific file within a profile, including the timestamp at which each version was introduced and, if applicable, when it was superseded or deleted, presented as an aligned table with column headers so each field lines up across rows, with each row's change kind rendered in a style that distinguishes it from the other kinds, and its size right-aligned within its column. WHEN a file path was previously tracked but no version records currently remain for it (for example, after retention has pruned the snapshots that referenced it), the system SHALL report that no version history remains for that path, rather than displaying an empty table.

#### Scenario: Viewing a file's history
- **WHEN** a user requests the version history of a file path within a profile
- **THEN** the system displays each recorded version of that file with its timestamp, most recent first, as a row of an aligned table

#### Scenario: Change kind is visually distinguished
- **WHEN** a file's version history includes a mix of added, changed, moved, and deleted versions
- **THEN** each row's change kind is rendered in a style that distinguishes it from the other kinds, so the kind of change is visible at a glance

#### Scenario: No version history remains for a previously tracked path
- **WHEN** a user requests the version history for a path that was previously tracked but for which no version records currently remain
- **THEN** the system reports that no version history is recorded for that path, rather than displaying an empty table
