## MODIFIED Requirements

### Requirement: Symlinks and junctions are not followed
The system SHALL NOT follow symbolic links, junctions, or other reparse points encountered while
scanning sources; it SHALL record their existence and target path without traversing into or
copying the linked content. A symlink/junction encountered during a scan SHALL be treated as
observed for that run, so it is never misclassified as a deletion of whatever previously existed
at that path. A path previously tracked as a regular file that is replaced by a symlink or
junction SHALL be recorded as a distinct, non-deleted state change, not silently dropped from the
manifest's current view.

#### Scenario: Symlink encountered during scan
- **WHEN** a source directory contains a symbolic link or junction
- **THEN** the backup run completes without traversing the link's target, and records that a link
  existed at that path

#### Scenario: Previously tracked file replaced by a symlink
- **WHEN** a path that was tracked as a regular file in the previous snapshot is now a symbolic
  link or junction at the same path
- **THEN** the backup run does not classify that path as deleted, and instead records that it is
  now a link, distinct from its prior tracked content

#### Scenario: Symlink present across multiple runs
- **WHEN** a source directory contains a symbolic link or junction that remains present, unchanged,
  across two consecutive backup runs
- **THEN** the second run does not classify the link's path as newly added, newly deleted, or
  changed
