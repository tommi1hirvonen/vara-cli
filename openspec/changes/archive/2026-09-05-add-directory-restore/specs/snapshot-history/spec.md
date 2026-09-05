## ADDED Requirements

### Requirement: Restore a directory at a given date
The system SHALL support restoring an entire directory (subtree) at once, instead of a single
file, when the user passes an explicit `--recursive` flag alongside the restore command's
directory path. In this mode, the system SHALL reconstruct the exact set of tracked paths that
existed under that directory as of the requested date (or the current state, if no date is
given): every path tracked as live at that date SHALL be written to the destination, including a
path later deleted from the profile, and a path not yet added as of that date SHALL be excluded.
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

## MODIFIED Requirements

### Requirement: Restore never writes into the live mirror
The system SHALL refuse to restore any content to a destination path that resolves inside the
profile's live mirror, regardless of whether an overwrite override was given, so that a restore
can never corrupt the very data it exists to recover. For a recursive directory restore, this
guard SHALL apply independently to every path's own computed destination, not only to a single
top-level destination.

#### Scenario: Destination resolves inside the profile's live mirror
- **WHEN** a user requests a restore to a destination path that resolves to a location inside the
  profile's live mirror
- **THEN** the system reports a clear error explaining that restoring into the live mirror is not
  permitted, does not write any content, and does not prompt for confirmation

#### Scenario: A recursive restore's computed destination resolves inside the live mirror
- **WHEN** a recursive restore's destination directory, or any individual tracked path's own
  computed destination within it, resolves to a location inside the profile's live mirror
- **THEN** the system reports the same clear error, and performs no part of the restore

### Requirement: Interactive version selection when restoring
WHEN a user runs the restore command for a single file (that is, without `--recursive`) without
specifying a version id or an "as of" date, and the session is interactive, the system SHALL
present the path's recorded version history as a selectable list and restore the version the
user selects, instead of reporting an error. This interactive picker SHALL NOT apply to a
recursive directory restore, which has no single per-file version history to select from.

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
