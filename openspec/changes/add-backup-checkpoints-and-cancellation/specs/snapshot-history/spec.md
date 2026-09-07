## MODIFIED Requirements

### Requirement: List snapshots
The system SHALL provide a command that lists the recorded snapshots for a profile, including each snapshot's timestamp and summary statistics, presented as an aligned table with column headers so each field lines up across rows, with each row's status rendered in the severity style matching that snapshot's outcome (per the `cli-presentation` capability), and numeric fields right-aligned within their column. Listing snapshots SHALL NOT itself create the profile's backing storage (its manifest database or on-disk profile directory) as a side effect; it is a read-only operation.

#### Scenario: Listing snapshot history
- **WHEN** a user requests the snapshot history for a profile that has completed at least one backup run
- **THEN** the system displays each recorded snapshot's timestamp and summary statistics, such as files changed and bytes transferred, as a row of an aligned table

#### Scenario: No snapshots recorded
- **WHEN** a user requests the snapshot history for a profile that has not yet completed any backup run
- **THEN** the system reports that no snapshots are recorded, rather than displaying an empty table

#### Scenario: Snapshot status reflects outcome severity
- **WHEN** the listed snapshots include a mix of complete, failed, cancelled, and in-progress runs
- **THEN** each row's status is rendered in the severity style corresponding to that snapshot's outcome (for example, a failed run's status in the hard-error style, and a cancelled run's status in the same warning style used for a partial-failure outcome), so outcomes are distinguishable at a glance without reading the status text

#### Scenario: Cancelled run is distinguished from a failed run
- **WHEN** the listed snapshots include one run cancelled via Ctrl+C and one run that failed due to an unhandled error
- **THEN** the cancelled run's status renders in the warning style rather than the hard-error style, so a user can tell a deliberate stop apart from a crash at a glance

#### Scenario: Numeric statistics are right-aligned
- **WHEN** the snapshot listing includes statistics of varying digit counts (for example, file counts and byte totals)
- **THEN** each numeric column's values are right-aligned within their column, so the digits line up regardless of value length

#### Scenario: Listing snapshots for a profile that has never completed a backup run
- **WHEN** a user requests the snapshot history for a profile whose backing storage does not yet
  exist because it has never completed a backup run
- **THEN** the system reports that no snapshots are recorded, and does not create the profile's
  backing storage as a result of the request
