## ADDED Requirements

### Requirement: A profile's own sources must not overlap each other
A profile's source paths SHALL NOT overlap one another: no source path may be equal to, an
ancestor of, or a descendant of any other source path within the same profile. Path comparison
SHALL be case-insensitive and SHALL treat a path with a trailing directory separator the same as
one without, consistent with the existing target-vs-source overlap check. This check does not
consider source paths belonging to any other profile.

#### Scenario: Two source paths are identical
- **WHEN** a profile defines two source entries with the same path
- **THEN** the system reports a validation error identifying the profile and the duplicated
  source path, and performs no action for that profile

#### Scenario: One source path is nested inside another source path
- **WHEN** a profile defines a source path that is a subdirectory of another of that profile's
  own source paths (for example, `C:\Users\me` and `C:\Users\me\Documents`)
- **THEN** the system reports a validation error identifying the profile and both offending
  source paths, and performs no action for that profile

#### Scenario: Case and trailing separator differences still detected as overlap
- **WHEN** two of a profile's source paths refer to the same or a nested location but differ only
  in letter casing or a trailing directory separator (for example, `C:\Data\` and `c:\Data`)
- **THEN** the system reports a validation error identifying the profile and both offending
  source paths, and performs no action for that profile

#### Scenario: Non-overlapping sources accepted
- **WHEN** none of a profile's source paths shares an ancestor/descendant relationship with any
  other of that profile's own source paths
- **THEN** the system accepts the profile without reporting a source overlap error

#### Scenario: Overlap with a different profile's source is not checked
- **WHEN** one profile's source path overlaps a source path that belongs only to a different
  profile in the same configuration file
- **THEN** the system does not report an overlap error for that combination
