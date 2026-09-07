# snapshot-history Specification

## Purpose

Lets a user see what backups have been taken over time and recover a file's content as it existed at a specific point in the past, independent of what the live mirror currently contains.

## Requirements

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
The system SHALL refuse to restore any content to a destination path that resolves inside the
profile's live mirror, regardless of whether an overwrite override was given, so that a restore
can never corrupt the very data it exists to recover. For a recursive directory restore, this
guard SHALL apply independently to every path's own computed destination, not only to a single
top-level destination. Resolving whether a destination is inside the mirror SHALL account for
reparse points (symlinks and junctions): a destination path that lexically appears to lie outside
the mirror, but whose real, final location - after following every reparse point along its path -
lies inside the mirror, SHALL still be treated as inside the mirror and refused.

#### Scenario: Destination resolves inside the profile's live mirror
- **WHEN** a user requests a restore to a destination path that resolves to a location inside the
  profile's live mirror
- **THEN** the system reports a clear error explaining that restoring into the live mirror is not
  permitted, does not write any content, and does not prompt for confirmation

#### Scenario: A recursive restore's computed destination resolves inside the live mirror
- **WHEN** a recursive restore's destination directory, or any individual tracked path's own
  computed destination within it, resolves to a location inside the profile's live mirror
- **THEN** the system reports the same clear error, and performs no part of the restore

#### Scenario: Destination reached only through a reparse point into the mirror
- **WHEN** a user requests a restore to a destination path that does not lexically start with the
  profile's live mirror path, but that path passes through a symlink or junction whose real,
  final location lies inside the profile's live mirror
- **THEN** the system reports the same clear error, and performs no part of the restore

### Requirement: Restore writes are atomic
The system SHALL write a restored file's content to its destination in a way that never leaves the
destination partially written: the destination SHALL either end up with the fully restored content,
or - if the write is interrupted for any reason - be left exactly as it was before the restore was
attempted. An existing destination file SHALL NOT be removed before the restored content has been
fully and successfully staged.

#### Scenario: Restore completes successfully over an existing destination file
- **WHEN** a user restores content to a destination that already contains a file, with overwrite
  authorized
- **THEN** the destination ends up containing exactly the restored content, and at no point during
  the operation is the destination left empty or missing

#### Scenario: Restore is interrupted while writing over an existing destination file
- **WHEN** a restore to a destination that already contains a file is interrupted before the
  restored content has been fully written (for example, the process is terminated, or the write
  fails partway through)
- **THEN** the destination file retains its original content, unchanged

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
in any of the following forms. WHEN the current working directory resolves to a location inside
the profile's mirror, the system SHALL try, in order: (1) the input resolved against the current
working directory to an absolute path, and, since that absolute path falls within the profile's
mirror, treated as a mirror-relative path by stripping the mirror root; (2) if that does not match
a recorded path, the input taken exactly as given, matched against recorded history literally; (3)
otherwise, treated as an absolute source path and converted to its mirror-relative form the same
way the backup pipeline derives mirror paths from source paths. WHEN the current working directory
does not resolve to a location inside the profile's mirror, the system SHALL try, in order: (1)
the input taken exactly as given, matched against recorded history literally; (2) otherwise,
resolved against the current working directory to an absolute path and, if that absolute path
happens to fall within the profile's mirror despite the working directory not being inside it (for
example, an absolute path typed directly), treated as a mirror-relative path by stripping the
mirror root; (3) otherwise, treated as an absolute source path and converted to its mirror-relative
form the same way the backup pipeline derives mirror paths from source paths. In every case, the
system uses the first interpretation that matches a recorded path; if none of these
interpretations matches a recorded path, the system reports that no history exists for the given
path.

#### Scenario: Path given in its mirror-relative form
- **WHEN** a user's current working directory is not inside the profile's mirror, and supplies a
  path that, taken literally, matches a path recorded in the profile's history
- **THEN** the system uses that literal path

#### Scenario: Path relative to a working directory inside the mirror
- **WHEN** a user's current working directory is inside the profile's mirror and supplies a path
  relative to that working directory (or an absolute path already inside the mirror) that, once
  resolved against the working directory and stripped of the mirror root, matches a path recorded
  in the profile's history
- **THEN** the system uses that cwd-relative interpretation, even if the input, taken literally,
  would also match a different path recorded elsewhere in the profile's history

#### Scenario: Path relative to a working directory inside the mirror, no cwd-relative match
- **WHEN** a user's current working directory is inside the profile's mirror, and the cwd-relative
  interpretation does not match a recorded path
- **THEN** the system falls back to trying the input literally, and then as an absolute source
  path, in that order

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

### Requirement: Single confirmation for a directory restore
Because a recursive restore can write and remove many files in one operation, the system SHALL
require exactly one explicit confirmation for the whole operation - reporting how many files will
be written and how many, if any, will be removed from the destination - instead of the per-file
overwrite confirmation used for a single-file restore. `--force` SHALL skip this confirmation the
same way it already skips a single-file restore's overwrite confirmation.

#### Scenario: Interactive confirmation before a directory restore
- **WHEN** a user requests a recursive restore in an interactive session without `--force`
- **THEN** the system reports the number of files that will be written and the number that will
  be removed from the destination, and prompts once for confirmation before making any change,
  defaulting to not proceeding if the user provides no explicit answer

#### Scenario: Explicit override skips the confirmation
- **WHEN** a user requests a recursive restore with `--force`
- **THEN** the system performs the restore, including any file removals, without prompting

#### Scenario: User declines the directory restore confirmation
- **WHEN** a user is prompted to confirm a recursive restore and responds negatively, or provides
  no answer
- **THEN** the system makes no change at the destination, reports that the restore was cancelled,
  and exits successfully rather than reporting an error

#### Scenario: Non-interactive session without an explicit override
- **WHEN** a user requests a recursive restore with a non-interactive input stream and without
  `--force`
- **THEN** the system reports a clear error explaining that an explicit `--force` override is
  required, and makes no change at the destination

### Requirement: Show a version's content directly
The system SHALL provide a command that streams a specific version's content - selected by
version id or by an "as of" date, the same as restore - directly to standard output, without
writing it to any file, so a user can inspect a historical version's content without performing a
restore. Before streaming, WHEN standard output is an interactive terminal (not redirected to a
file or another program) and the resolved version's content is detected as binary from a bounded
initial sample of its bytes, the system SHALL refuse to stream it, unless the user has explicitly
requested to force binary output. WHEN standard output is redirected, the system SHALL stream the
content regardless of whether it is binary, since there is no terminal to protect.

#### Scenario: Showing a text file's historical content
- **WHEN** a user requests a specific version of a file's content to be shown
- **THEN** the system writes that version's content to standard output without creating or
  modifying any file on disk

#### Scenario: Showing an unknown path or version
- **WHEN** a user requests a version to be shown for a path never tracked, or a version or date
  that does not exist
- **THEN** the system reports the same clear error already required for restore's equivalent
  case, rather than partial or misleading output

#### Scenario: Refusing binary content to an interactive terminal
- **WHEN** a user requests a version to be shown whose content is detected as binary from a
  bounded initial sample of its bytes, and standard output is an interactive terminal
- **THEN** the system reports a clear error naming the path and explaining that the content
  appears to be binary, without writing any of it to standard output

#### Scenario: Binary content streamed when output is redirected
- **WHEN** a user requests a version to be shown whose content is detected as binary, and
  standard output is redirected to a file or another program rather than an interactive terminal
- **THEN** the system streams the content exactly as it would for text content, without refusing

#### Scenario: Binary content forced to an interactive terminal
- **WHEN** a user requests a version to be shown whose content is detected as binary, standard
  output is an interactive terminal, and the user has explicitly requested to force binary output
- **THEN** the system streams the content instead of refusing

### Requirement: Diff two versions of a file
The system SHALL provide a command that shows a textual difference between two versions of the
same path, each selected by version id or by an "as of" date, so a user can see what changed
between two points in a file's history without restoring either version. Before reading either
version's full content, the system SHALL refuse the comparison - naming the offending side(s) and,
for an oversized refusal, its size - when either version's recorded size exceeds 10 MB, or when
either version's content is detected as binary from a bounded initial sample of its bytes, rather
than buffering a large or binary version fully into memory and diffing it as if it were text.

#### Scenario: Diffing two recorded versions
- **WHEN** a user requests a diff between two versions of the same path, each identified by a
  version id or an "as of" date
- **THEN** the system displays a textual difference between the two versions' content

#### Scenario: Diffing an unknown path or version
- **WHEN** a user requests a diff involving a path never tracked, or a version or date that does
  not exist
- **THEN** the system reports the same clear error already required for restore's equivalent
  case, rather than partial or misleading output

#### Scenario: Refusing an oversized version
- **WHEN** a user requests a diff and at least one of the two resolved versions has a recorded
  size exceeding the diff size limit
- **THEN** the system reports a clear error naming which side(s) exceed the limit and their
  size, without reading either version's full content into memory

#### Scenario: Refusing binary content
- **WHEN** a user requests a diff and at least one of the two resolved versions is detected as
  binary content from a bounded initial sample of its bytes
- **THEN** the system reports a clear error naming which side(s) are binary, without producing a
  byte-level "diff" of that content

#### Scenario: Both versions exceed the size limit
- **WHEN** a user requests a diff and both resolved versions have a recorded size exceeding the
  diff size limit
- **THEN** the system reports a single clear error naming both sides and their sizes, rather than
  only the first side checked

#### Scenario: One version too large, the other binary
- **WHEN** a user requests a diff where one resolved version exceeds the diff size limit and the
  other resolved version (within the size limit) is detected as binary content
- **THEN** the system reports a clear error identifying each side's specific problem, rather than
  a generic or misleading message
