## MODIFIED Requirements

### Requirement: Restore a file at a given version or date
The system SHALL provide a command that extracts a specific file's content as it existed at a
given version or as of a given date, without modifying the current live mirror. The system SHALL
NOT overwrite an existing file at the requested destination unless the user has explicitly
authorized the overwrite, either by passing an explicit override or by confirming an interactive
prompt. A version id and an "as of" date SHALL be mutually exclusive ways of identifying which
version to restore; supplying both SHALL be rejected with a clear error, and no restore SHALL be
performed. If the resolved version is a symlink/junction entry rather than one with actual stored
content, the system SHALL report a clear error explaining that this version has no content to
restore, rather than attempting extraction and failing with an unrelated internal error.

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

#### Scenario: Requested version is a symlink or junction entry
- **WHEN** a user requests a restore of a specific version id, or as of a date, that resolves to a
  symlink/junction entry rather than one with stored content
- **THEN** the system reports a clear error explaining that the resolved version is a link with no
  restorable content, and writes nothing to the destination

### Requirement: Interactive version selection when restoring
WHEN a user runs the restore command for a single file (that is, without `--recursive`) without
specifying a version id or an "as of" date, and the session is interactive, the system SHALL
present the path's recorded version history as a selectable list and restore the version the
user selects, instead of reporting an error. This interactive picker SHALL NOT apply to a
recursive directory restore, which has no single per-file version history to select from. The
selectable list SHALL exclude both deleted versions and symlink/junction versions, since neither
has restorable content.

#### Scenario: Restoring interactively without a version or date
- **WHEN** a user runs the restore command for a path with recorded history, specifying neither
  a version id nor an "as of" date, in an interactive session
- **THEN** the system presents the path's recorded versions as a selectable list and restores
  whichever version the user selects

#### Scenario: Restoring non-interactively without a version or date
- **WHEN** a user runs the restore command specifying neither a version id nor an "as of" date,
  and the session is not interactive
- **THEN** the system reports the existing clear error requiring one of `--at` or `--version`,
  rather than attempting to present a selectable list

#### Scenario: Recursive restore without a version or date does not prompt for a version
- **WHEN** a user runs a recursive restore of a directory specifying neither a version id nor an
  "as of" date
- **THEN** the system reconstructs the directory's current tracked state, per the "Restore a
  directory at a given date" requirement's "Recursive restore without an explicit date" scenario,
  rather than presenting an interactive version picker

#### Scenario: Selectable list excludes symlink/junction versions
- **WHEN** a user runs the restore command interactively for a path whose recorded history
  includes a version where the path was a symlink or junction
- **THEN** the system's selectable list omits that version, alongside any deleted version,
  since neither has content that can be restored

### Requirement: Restore a directory at a given date
The system SHALL support restoring an entire directory (subtree) at once, instead of a single
file, when the user passes an explicit `--recursive` flag alongside the restore command's
directory path. In this mode, the system SHALL reconstruct the exact set of tracked paths that
existed under that directory as of the requested date (or the current state, if no date is
given): every path tracked as live at that date SHALL be written to the destination, including a
path later deleted from the profile, and a path not yet added as of that date SHALL be excluded.
A path tracked as live at that date as a symlink/junction entry SHALL NOT be written (it has no
stored content to extract); it SHALL instead be counted and reported as skipped, separately from
the written and removed counts, so its absence from the destination is visible rather than silent.
Any path the system currently tracks as live under that directory but which was not live as of
the requested date SHALL be removed from the destination, so the destination ends up matching the
requested point in time exactly rather than a merge of old and current tracked content. This mode
SHALL be mutually exclusive with `--version`, which identifies a single file's specific version
and has no meaning for a whole subtree.

#### Scenario: Restoring a directory to a new destination
- **WHEN** a user requests a recursive restore of a directory as of a given date, to a
  destination that does not already exist
- **THEN** the system writes every path tracked as live under that directory as of that date to
  the destination, preserving the directory's relative structure

#### Scenario: Restoring a directory brings back since-deleted files
- **WHEN** a directory being recursively restored as of a given date contains a file that was
  live at that date but has since been deleted from the profile
- **THEN** the system still writes that file's content, as it existed at that date, to the
  destination

#### Scenario: Restoring a directory excludes files added afterward
- **WHEN** a directory being recursively restored as of a given date contains a file that was
  added to the profile only after that date
- **THEN** the system does not write that file to the destination

#### Scenario: Restoring a directory removes tracked content that did not exist at that date
- **WHEN** a directory being recursively restored as of a given date is restored to a destination
  that already contains a file the system currently tracks as live under that directory, but
  which was not live as of the requested date
- **THEN** the system removes that file from the destination as part of the restore, so the
  destination reflects only the paths that existed at the requested date

#### Scenario: Restoring a directory in place
- **WHEN** a user requests a recursive, in-place restore of a directory as of a given date
- **THEN** the system restores each tracked path under that directory to its own original
  absolute source location, derived the same way a single file's in-place restore derives its
  location, rather than to a single common destination directory

#### Scenario: Recursive restore combined with a specific version id
- **WHEN** a user requests a recursive restore and also supplies `--version`
- **THEN** the system reports a clear error explaining that `--recursive` and `--version` are
  mutually exclusive, and performs no restore

#### Scenario: Recursive restore of a directory never tracked
- **WHEN** a user requests a recursive restore of a directory path under which no tracked path,
  at any point in history, falls
- **THEN** the system reports that no such directory is recorded in the backup, rather than
  restoring nothing silently

#### Scenario: Recursive restore without an explicit date
- **WHEN** a user requests a recursive restore of a directory without supplying `--at`
- **THEN** the system reconstructs the directory's current tracked state (its live paths as of
  now) rather than reporting an error or prompting for a date

#### Scenario: Recursive restore of a directory containing a symlink/junction
- **WHEN** a directory being recursively restored as of a given date contains a path tracked as
  live at that date as a symlink or junction
- **THEN** the system does not write that path to the destination, does not treat this as a
  failure, and reports it as a skipped link separately from the written and removed counts
