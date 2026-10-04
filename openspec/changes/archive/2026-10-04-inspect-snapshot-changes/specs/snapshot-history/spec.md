## ADDED Requirements

### Requirement: Inspect changes recorded by a snapshot
The system SHALL provide a `vara snapshot <id> [directory]` command that shows the recorded
changes for one snapshot, optionally scoped to a directory. The directory argument SHALL accept
the same mirror-relative, absolute source, and current-directory-relative forms as directory
browsing, and omission SHALL select the mirror root. The report SHALL identify the snapshot and
its status, and SHALL present a sparse directory tree in which each directory's counts include
the change events recorded directly in that directory and all its descendants. The counts SHALL
distinguish added, changed, moved, deleted, and linked entries. Configured source roots that
correspond to a directory in the tree SHALL be identifiable as sources. The report SHALL NOT
modify or create profile backing storage.

The command SHALL support `--files` to show the changed file paths and their change kinds in
addition to the directory rollups; moved entries SHALL include their previous path when recorded.
Without `--files`, the report SHALL omit individual file paths. If the snapshot id is unknown, or
no recorded change events match the requested directory scope, the command SHALL report that
condition explicitly instead of rendering an empty tree.

#### Scenario: Showing a snapshot's directory rollups
- **WHEN** a user runs `vara snapshot <id>` for a recorded snapshot containing changes
- **THEN** the system identifies the snapshot and displays a sparse directory tree with the
  added, changed, moved, deleted, and linked event counts rolled up for each affected directory

#### Scenario: Identifying configured source roots
- **WHEN** a changed directory corresponds to a source root in the profile's current
  configuration
- **THEN** the report identifies that directory as a configured source root while retaining its
  change counts

#### Scenario: Scoping the report to a directory
- **WHEN** a user runs `vara snapshot <id> <directory>` for a directory containing recorded
  changes in that snapshot
- **THEN** the report includes only events under that directory and shows counts relative to
  that scope

#### Scenario: Showing changed file paths on request
- **WHEN** a user runs `vara snapshot <id> [directory] --files`
- **THEN** the report includes the changed file paths and their change kinds alongside the same
  directory rollups, and includes a moved entry's previous path when one is recorded

#### Scenario: Omitting file paths by default
- **WHEN** a user runs `vara snapshot <id> [directory]` without `--files`
- **THEN** the report shows directory rollups without listing individual changed file paths

#### Scenario: Unknown snapshot id
- **WHEN** a user requests a snapshot id that is not recorded for the selected profile
- **THEN** the system reports that the snapshot is unknown and does not render an empty report

#### Scenario: No changes in the requested scope
- **WHEN** a user requests a recorded snapshot and directory scope with no matching change events
- **THEN** the system reports that no changes are recorded for that snapshot in the requested
  scope, rather than rendering an empty tree

#### Scenario: Inspecting snapshots without backing storage
- **WHEN** a user requests snapshot details for a profile with no recorded snapshot storage
- **THEN** the system reports that the snapshot is unknown and does not create profile backing
  storage
