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
