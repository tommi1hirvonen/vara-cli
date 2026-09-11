## MODIFIED Requirements

### Requirement: Unknown path or version reported clearly
WHEN a user requests history or restoration for a path never tracked, or a version or date that
does not exist, the system SHALL report a clear error rather than partial or misleading results.
WHEN a user requests a single-file `restore` (i.e. without `--recursive`) for a path that does not
match any tracked file, but does match a tracked directory - some tracked path, live or deleted,
falls under it - the system SHALL report that the path is a directory and that `--recursive` is
required, distinct from the "no history exists" message used when the path matches neither a
tracked file nor a tracked directory.

#### Scenario: Path never backed up
- **WHEN** a user requests version history for a path that was never part of any recorded
  snapshot
- **THEN** the system reports that no history exists for that path

#### Scenario: Restore without --recursive given a tracked directory path
- **WHEN** a user runs `restore <path>` without `--recursive`, and `<path>` (after the same
  flexible-path resolution single-file `restore` already applies) does not match any tracked
  file, but some tracked path - live or deleted - falls under it as a directory
- **THEN** the system reports that `<path>` is a directory and that `--recursive` must be passed
  to restore it, and does not report the generic "no history exists" message
