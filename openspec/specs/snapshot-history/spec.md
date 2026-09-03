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

### Requirement: Flexible path input for history, restore, show, and diff
The system SHALL accept a path argument to the `history`, `restore`, `show`, and `diff` commands
in any of the following forms, resolving it by trying each rule in order and using the first one
that matches a recorded path: (1) exactly as given, matched against recorded history literally
(the mirror-relative form already supported); (2) if no literal match is found, resolved against
the current working directory to an absolute path, and, if that absolute path falls within the
profile's mirror, treated as a mirror-relative path by stripping the mirror root; (3) otherwise,
treated as an absolute source path and converted to its mirror-relative form the same way the
backup pipeline derives mirror paths from source paths. If none of these interpretations matches
a recorded path, the system reports that no history exists for the given path.

#### Scenario: Path given in its mirror-relative form
- **WHEN** a user supplies a path that, taken literally, matches a path recorded in the profile's
  history
- **THEN** the system uses that literal path, regardless of the current working directory

#### Scenario: Path relative to a working directory inside the mirror
- **WHEN** a user's current working directory is inside the profile's mirror and supplies a path
  relative to that working directory (or an absolute path already inside the mirror) that does
  not literally match a recorded path
- **THEN** the system resolves the supplied path against the working directory, strips the
  mirror root, and uses the result as the mirror-relative path

#### Scenario: Path relative to a working directory inside a source
- **WHEN** a user's current working directory is inside one of the profile's configured sources
  and supplies a path relative to that working directory that does not literally match a
  recorded path and does not resolve inside the mirror
- **THEN** the system resolves the supplied path against the working directory to an absolute
  source path, converts it to its mirror-relative form, and uses that as the resolved path

#### Scenario: Absolute source path given directly
- **WHEN** a user supplies an absolute path from one of the profile's sources (for example,
  copied from a file explorer or another tool) instead of its mirror-relative form
- **THEN** the system converts it to its mirror-relative form and uses that as the resolved path

#### Scenario: No interpretation matches recorded history
- **WHEN** none of the path's possible interpretations matches a path recorded in the profile's
  history
- **THEN** the system reports that no history exists for the given path

### Requirement: Restore to the original source location
The system SHALL support restoring a file's historical content back to its original absolute
source location - the location it would occupy in the live source tree, derived from its
mirror-relative path - as an alternative to writing it to an explicitly specified destination.
This mode SHALL be requested explicitly, by a flag mutually exclusive with the existing
destination option, and is otherwise governed by the same overwrite and mirror-containment
guards as a restore to an explicit destination.

#### Scenario: Restoring in place to a location that no longer exists
- **WHEN** a user requests an in-place restore of a file whose original source location does not
  currently contain a file (for example, because it was deleted)
- **THEN** the system writes the restored content to that location without requiring
  confirmation, creating any missing parent directories

#### Scenario: Restoring in place over an existing file
- **WHEN** a user requests an in-place restore of a file whose original source location
  currently contains a file
- **THEN** the system applies the same overwrite confirmation behavior already required for a
  restore to an explicit destination, refusing to overwrite the existing file without an
  explicit override or confirmation

#### Scenario: In-place restore and an explicit destination are both given
- **WHEN** a user supplies both an in-place restore request and an explicit destination for the
  same restore
- **THEN** the system reports a clear error explaining that the two are mutually exclusive, and
  performs no restore

### Requirement: Interactive version selection when restoring
WHEN a user runs the restore command without specifying a version id or an "as of" date, and the
session is interactive, the system SHALL present the path's recorded version history as a
selectable list and restore the version the user selects, instead of reporting an error.

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

### Requirement: Show a version's content directly
The system SHALL provide a command that streams a specific version's content - selected by
version id or by an "as of" date, the same as restore - directly to standard output, without
writing it to any file, so a user can inspect a historical version's content without performing a
restore.

#### Scenario: Showing a text file's historical content
- **WHEN** a user requests a specific version of a file's content to be shown
- **THEN** the system writes that version's content to standard output without creating or
  modifying any file on disk

#### Scenario: Showing an unknown path or version
- **WHEN** a user requests a version to be shown for a path never tracked, or a version or date
  that does not exist
- **THEN** the system reports the same clear error already required for restore's equivalent
  case, rather than partial or misleading output

### Requirement: Diff two versions of a file
The system SHALL provide a command that shows a textual difference between two versions of the
same path, each selected by version id or by an "as of" date, so a user can see what changed
between two points in a file's history without restoring either version.

#### Scenario: Diffing two recorded versions
- **WHEN** a user requests a diff between two versions of the same path, each identified by a
  version id or an "as of" date
- **THEN** the system displays a textual difference between the two versions' content

#### Scenario: Diffing an unknown path or version
- **WHEN** a user requests a diff involving a path never tracked, or a version or date that does
  not exist
- **THEN** the system reports the same clear error already required for restore's equivalent
  case, rather than partial or misleading output
