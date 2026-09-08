## MODIFIED Requirements

### Requirement: Source and target paths must be absolute
A profile's target root, and every one of its source paths, SHALL be a fully-qualified, rooted
(absolute) path. A path that is relative to some unspecified working directory has no sensible
meaning as a backup source or target and SHALL be rejected at validation time rather than silently
resolved against the process's current working directory when a backup, scan, or other operation
later runs.

Once accepted, a target root or source path SHALL be stored in canonical absolute form: any
forward slashes, redundant (doubled) path separators, or `.`/`..` segments that the configured
value contains SHALL be normalized away, so that every downstream consumer (the file system
scanner, the mirror-path mapper, and snapshot history lookups) observes and records a single
consistent path string for a given location. Two differently-written configured values that denote
the same location (for example, one using forward slashes and one using the platform's native
separator) SHALL normalize to the identical stored string. This normalization does not strip a
single trailing directory separator - a configured value ending in a separator remains distinct
from the same value without one.

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

#### Scenario: Forward-slash source path normalized to the native separator
- **WHEN** a profile's source path is written with forward slashes (for example,
  `C:/Users/me/Docs`)
- **THEN** the system accepts the profile, and every downstream use of that source path (scanning,
  mirror-path derivation, and snapshot history lookups) observes the path with the platform's
  native directory separator throughout, rather than a mix of `/` and `\`

#### Scenario: Forward-slash target root normalized to the native separator
- **WHEN** a profile's target root is written with forward slashes (for example, `C:/Backups/vara`)
- **THEN** the system accepts the profile, and every downstream use of that target root observes
  the path with the platform's native directory separator throughout

#### Scenario: Equivalent path forms normalize to the same stored value
- **WHEN** two profiles (or a profile reloaded after an edit) configure a target root or source
  path for the same location using different but equivalent forms (for example, differing only in
  separator style, a doubled separator, or a redundant `.` segment)
- **THEN** the system stores the same canonical path string for both, rather than two lexically
  different strings that a later exact-match lookup (such as snapshot history) would treat as
  different locations

#### Scenario: Trailing separator is not stripped
- **WHEN** a profile's target root or source path is written with a single trailing directory
  separator (for example, `C:\Backups\vara\`)
- **THEN** the system accepts the profile, and the stored path retains that trailing separator
  rather than being treated as identical to the same value without one
