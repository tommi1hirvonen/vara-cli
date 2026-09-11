## MODIFIED Requirements

### Requirement: Interactive version selection when restoring
WHEN a user runs the restore command for a single file (that is, without `--recursive`) without
specifying a version id or an "as of" date, and the session is interactive, the system SHALL
present the path's recorded version history as a selectable list and restore the version the
user selects, instead of reporting an error. A recursive directory restore has its own
interactive snapshot picker instead, covered by the "Interactive and explicit snapshot selection
for a directory restore" requirement, since a directory has no single per-file version history to
select from. The selectable list SHALL exclude both deleted versions and symlink/junction
versions, since neither has restorable content.

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

#### Scenario: Selectable list excludes symlink/junction versions
- **WHEN** a user runs the restore command interactively for a path whose recorded history
  includes a version where the path was a symlink or junction
- **THEN** the system's selectable list omits that version, alongside any deleted version,
  since neither has content that can be restored

#### Scenario: Recursive restore without a version or date does not prompt for a version
- **WHEN** a user runs a recursive restore of a directory specifying neither a version id nor an
  "as of" date
- **THEN** the system does not present this single-file version picker for the directory (it has
  no single per-file version history to select from); it instead follows the "Interactive and
  explicit snapshot selection for a directory restore" requirement, which governs whatever the
  system presents or restores in that case

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
and has no meaning for a whole subtree. The requested point in time may be given explicitly via
`--at`, selected via `--snapshot <id>`, or, in an interactive session with neither given, chosen
from a presented list of candidate snapshots - all covered in full by the "Interactive and
explicit snapshot selection for a directory restore" requirement.

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
- **WHEN** a user requests a recursive restore of a directory without supplying `--at` or
  `--snapshot`, and the session is not interactive
- **THEN** the system reconstructs the directory's current tracked state (its live paths as of
  now) without prompting for anything, since prompting requires an interactive session; an
  interactive session instead follows the "Interactive and explicit snapshot selection for a
  directory restore" requirement

#### Scenario: Recursive restore of a directory containing a symlink/junction
- **WHEN** a directory being recursively restored as of a given date contains a path tracked as
  live at that date as a symlink or junction
- **THEN** the system does not write that path to the destination, does not treat this as a
  failure, and reports it as a skipped link separately from the written and removed counts

## ADDED Requirements

### Requirement: Interactive and explicit snapshot selection for a directory restore
WHEN a user runs the restore command with `--recursive` in an interactive session, without
supplying `--at` or `--snapshot`, the system SHALL present a selectable list of candidate
snapshots for the requested directory instead of silently reconstructing the current tracked
state. A candidate snapshot is any recorded snapshot, in `Complete` or `Cancelled` status, for
which at least one file-version record exists under the requested directory; a snapshot in
`Running` or `Failed` status SHALL NOT be offered. The selectable list SHALL also include an
entry representing the directory's current tracked state, presented as the default selection so
that accepting it without changing the selection reproduces the same result as running the
command without `--at`, `--snapshot`, or an interactive session. Selecting an entry restores the
directory to that point in time using the same reconstruction behavior already required by the
"Restore a directory at a given date" requirement.

The system SHALL also support selecting a specific candidate snapshot non-interactively via a
`--snapshot <id>` option, mutually exclusive with `--at`, so the same point in time reachable
through the interactive list can be requested in a non-interactive or scripted session.

Each entry in the selectable list SHALL report, scoped to the requested directory rather than to
the whole profile: the counts of files added, changed, moved, and deleted by that snapshot, the
net change in bytes, the total number of files (excluding symlinks/junctions) the directory
contains as of that snapshot, the directory's total byte size as of that snapshot, and the total
number of symlink/junction entries the directory contains as of that snapshot, reported as its
own separate figure rather than folded into the file count. A snapshot in `Cancelled` status
SHALL be visually distinguished from one in `Complete` status in the selectable list, consistent
with the distinct styling already used for these statuses by the `snapshots` command.

#### Scenario: Presenting the directory snapshot picker interactively
- **WHEN** a user requests a recursive restore of a directory in an interactive session,
  supplying neither `--at` nor `--snapshot`
- **THEN** the system presents a selectable list of the directory's candidate snapshots, plus a
  current-tracked-state entry, and restores the point in time the user selects

#### Scenario: Accepting the default entry reproduces today's default behavior
- **WHEN** a user is presented with the directory snapshot picker and accepts the default,
  pre-selected entry without changing the selection
- **THEN** the system reconstructs the directory's current tracked state, exactly as it would
  without `--at`, `--snapshot`, or an interactive session

#### Scenario: Selecting a candidate snapshot restores its point in time
- **WHEN** a user selects a specific candidate snapshot, other than the current-tracked-state
  entry, from the directory snapshot picker
- **THEN** the system reconstructs the directory's tracked state as of that snapshot, following
  the same write/remove/skip behavior already required for restoring a directory at a given date

#### Scenario: Only snapshots that touched the directory are offered
- **WHEN** a user requests a recursive restore of a directory in an interactive session
- **THEN** the selectable list of candidate snapshots excludes any recorded snapshot with no
  file-version record under that directory, regardless of that snapshot's status

#### Scenario: Running and failed snapshots are never offered
- **WHEN** a directory has at least one file-version record belonging to a snapshot in `Running`
  or `Failed` status
- **THEN** that snapshot is not included in the directory snapshot picker, even if it also has
  file-version records under the directory

#### Scenario: Cancelled snapshots are visually distinguished
- **WHEN** the directory snapshot picker includes both `Complete` and `Cancelled` candidate
  snapshots
- **THEN** the system renders `Cancelled` entries with styling distinct from `Complete` entries,
  so the two are easy to tell apart at a glance

#### Scenario: Picker entries report directory-scoped statistics
- **WHEN** the directory snapshot picker is presented
- **THEN** each non-current entry reports the counts of files added, changed, moved, and deleted
  by that snapshot within the requested directory, the directory's total file count and byte size
  as of that snapshot, and the directory's total symlink/junction count as of that snapshot,
  reported separately from the file count

#### Scenario: Selecting a specific snapshot non-interactively
- **WHEN** a user requests a recursive restore of a directory and supplies `--snapshot <id>` for
  a candidate snapshot of that directory
- **THEN** the system reconstructs the directory's tracked state as of that snapshot, without
  requiring an interactive session

#### Scenario: `--snapshot` and `--at` are mutually exclusive
- **WHEN** a user supplies both `--snapshot` and `--at` to a recursive restore
- **THEN** the system reports a clear error explaining that the two are mutually exclusive, and
  performs no restore

#### Scenario: `--snapshot` without `--recursive` is rejected
- **WHEN** a user supplies `--snapshot` without also supplying `--recursive`
- **THEN** the system reports a clear error explaining that `--snapshot` only applies to a
  recursive directory restore, and performs no restore

#### Scenario: `--snapshot` naming a snapshot that never touched the directory
- **WHEN** a user supplies `--snapshot <id>` for a recursive restore, and the identified snapshot
  has no file-version record under the requested directory, or does not exist, or is in
  `Running` or `Failed` status
- **THEN** the system reports a clear error explaining that the snapshot is not a valid candidate
  for that directory, and performs no restore
