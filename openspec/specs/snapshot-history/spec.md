# snapshot-history Specification

## Purpose

Lets a user see what backups have been taken over time and recover a file's content as it existed at a specific point in the past, independent of what the live mirror currently contains.

## Requirements

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

### Requirement: Restore a file at a given version or date
The system SHALL provide a command that extracts a specific file's content as it existed at a
given version or as of a given date, without modifying the current live mirror. The system SHALL
NOT overwrite an existing file at the requested destination unless the user has explicitly
authorized the overwrite, either by passing an explicit override or by confirming an interactive
prompt.

#### Scenario: Restoring a previous version
- **WHEN** a user requests a file's content as of a date prior to its most recent change
- **THEN** the system writes that file's content, as it existed at that date, to a location
  specified by the user, and leaves the live mirror unchanged

#### Scenario: Destination does not already exist
- **WHEN** a user requests a restore to a destination path that does not currently exist
- **THEN** the system writes the restored content to that destination without requiring any
  confirmation

#### Scenario: Destination exists and an explicit overwrite override is given
- **WHEN** a user requests a restore to a destination path that already contains a file, and
  explicitly authorizes overwriting it
- **THEN** the system overwrites the existing file with the restored content without prompting

#### Scenario: Destination exists, running interactively, no explicit override given
- **WHEN** a user requests a restore to a destination path that already contains a file, does not
  explicitly authorize overwriting it, and the command is running with an interactive input
  stream
- **THEN** the system prompts the user for confirmation before overwriting, defaulting to not
  overwriting if the user provides no explicit answer

#### Scenario: User confirms the interactive overwrite prompt
- **WHEN** a user is prompted to confirm overwriting an existing destination file and responds
  affirmatively
- **THEN** the system overwrites the existing file with the restored content

#### Scenario: User declines the interactive overwrite prompt
- **WHEN** a user is prompted to confirm overwriting an existing destination file and responds
  negatively, or provides no answer
- **THEN** the system leaves the existing destination file unchanged, reports that the restore
  was cancelled, and exits successfully rather than reporting an error

#### Scenario: Destination exists, running non-interactively, no explicit override given
- **WHEN** a user requests a restore to a destination path that already contains a file, does not
  explicitly authorize overwriting it, and the command is running with a non-interactive input
  stream
- **THEN** the system reports a clear error explaining that the destination already exists and
  that an explicit overwrite override is required, and does not modify the existing destination
  file

### Requirement: Restore progress indication for large files
While a restore command is extracting a historical file's content to its destination, the system SHALL display a byte-based progress indicator reflecting bytes copied against the version's known total size, per the `progress-reporting` capability's incremental-progress requirement, so a restore of a large file does not appear to hang with no feedback. Because a version's total size is known before extraction begins, no separate scan or indeterminate phase is needed.

#### Scenario: Restoring a large file
- **WHEN** a user restores a historical version of a file large enough that its extraction takes a perceptible amount of time
- **THEN** the system displays a progress indicator that advances as the file's content is copied to the destination, starting immediately with the version's known total size rather than an indeterminate indicator

#### Scenario: Restoring a small file
- **WHEN** a user restores a historical version of a file small enough that its extraction completes almost immediately
- **THEN** the system completes the restore without the progress indicator producing a noticeable or distracting flash on screen

#### Scenario: Non-interactive or redirected output
- **WHEN** a restore command runs with output redirected or otherwise unable to render a live progress bar
- **THEN** the system falls back to plain, non-progress-bar output, per the `cli-presentation` capability's non-interactive fallback behavior

### Requirement: Restore never writes into the live mirror
The system SHALL refuse to restore a file to a destination path that resolves inside the
profile's live mirror, regardless of whether an overwrite override was given, so that a restore
can never corrupt the very data it exists to recover.

#### Scenario: Destination resolves inside the profile's live mirror
- **WHEN** a user requests a restore to a destination path that resolves to a location inside the
  profile's live mirror
- **THEN** the system reports a clear error explaining that restoring into the live mirror is not
  permitted, does not write any content, and does not prompt for confirmation

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
