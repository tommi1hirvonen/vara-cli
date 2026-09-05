## ADDED Requirements

### Requirement: Target root must not overlap a profile's own sources
A profile's target root SHALL NOT be equal to, an ancestor of, or a descendant of any of that
profile's own source paths. Path comparison SHALL be case-insensitive and SHALL treat a path with
a trailing directory separator the same as one without. This check does not consider paths
belonging to any other profile.

#### Scenario: Target equals a source path
- **WHEN** a profile's target root is identical to one of its own source paths
- **THEN** the system reports a validation error identifying the profile, the offending source
  path, and the target root, and performs no action for that profile

#### Scenario: Target is nested inside a source path
- **WHEN** a profile's target root is a subdirectory of one of its own source paths (for example,
  target `C:\Users\me\backup` with source `C:\Users\me\`)
- **THEN** the system reports a validation error identifying the profile, the offending source
  path, and the target root, and performs no action for that profile

#### Scenario: A source path is nested inside the target root
- **WHEN** one of a profile's own source paths is a subdirectory of that profile's target root (for
  example, target `C:\Users\me\backup` with source `C:\Users\me\backup\Documents`)
- **THEN** the system reports a validation error identifying the profile, the offending source
  path, and the target root, and performs no action for that profile

#### Scenario: Case and trailing separator differences still detected as overlap
- **WHEN** a profile's target root and a source path refer to the same or a nested location but
  differ only in letter casing or a trailing directory separator (for example, target
  `c:\backup\` and source `C:\Backup`)
- **THEN** the system reports a validation error identifying the profile, the offending source
  path, and the target root, and performs no action for that profile

#### Scenario: Non-overlapping target and sources accepted
- **WHEN** a profile's target root shares no ancestor/descendant relationship with any of its own
  source paths
- **THEN** the system accepts the profile without reporting an overlap error

#### Scenario: Overlap with a different profile's source is not checked
- **WHEN** a profile's target root overlaps a source path that belongs only to a different profile
  in the same configuration file
- **THEN** the system does not report an overlap error for that combination
