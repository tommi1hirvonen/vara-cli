## MODIFIED Requirements

### Requirement: Restore a file at a given version or date
The system SHALL provide a command that extracts a specific file's content as it existed at a
given version or as of a given date, without modifying the current live mirror. The system SHALL
NOT overwrite an existing file at the requested destination unless the user has explicitly
authorized the overwrite, either by passing an explicit override or by confirming an interactive
prompt. A version id and an "as of" date SHALL be mutually exclusive ways of identifying which
version to restore; supplying both SHALL be rejected with a clear error, and no restore SHALL be
performed.

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

#### Scenario: Both a version id and an as-of date are given
- **WHEN** a user supplies both a version id and an "as of" date for the same restore, show, or
  diff request
- **THEN** the system reports a clear error explaining that the two are mutually exclusive, and
  performs no restore, show, or diff
