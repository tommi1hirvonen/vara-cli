## ADDED Requirements

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
