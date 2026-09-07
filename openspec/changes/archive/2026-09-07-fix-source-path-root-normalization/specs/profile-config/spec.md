## ADDED Requirements

### Requirement: Source and target paths must be absolute
A profile's target root, and every one of its source paths, SHALL be a fully-qualified, rooted
(absolute) path. A path that is relative to some unspecified working directory has no sensible
meaning as a backup source or target and SHALL be rejected at validation time rather than silently
resolved against the process's current working directory when a backup, scan, or other operation
later runs.

#### Scenario: Relative target root rejected
- **WHEN** a profile's target root is a relative path (for example, `backup\` or `..\backup`)
- **THEN** the system reports a validation error identifying the profile, the target field, and
  the offending value, and performs no action for that profile

#### Scenario: Relative source path rejected
- **WHEN** one of a profile's source paths is a relative path (for example, `Documents` or
  `.\src`)
- **THEN** the system reports a validation error identifying the profile, the offending source
  path, and performs no action for that profile

#### Scenario: Absolute target and source paths accepted
- **WHEN** a profile's target root and every source path are fully-qualified, rooted paths (for
  example, `C:\Backups\vara` and `C:\Users\me\Documents`)
- **THEN** the system accepts the profile without reporting a rootedness error
