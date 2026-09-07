# profile-config Specification

## Purpose

Defines how Vara locates, loads, and validates the user's backup profile configuration - profiles, their sources, target, and retention settings - so that every other capability has a validated profile definition to act on.

## Requirements

### Requirement: Profile configuration file location
The system SHALL load profile configuration from `~/.vara/profiles.yml` by default.

#### Scenario: Default location used
- **WHEN** a user runs a Vara command without specifying an alternate configuration path
- **THEN** the system reads profiles from `~/.vara/profiles.yml`

#### Scenario: Configuration file missing
- **WHEN** `~/.vara/profiles.yml` does not exist
- **THEN** the system reports a clear error identifying the expected file location and performs no backup, history, restore, or prune action

### Requirement: Profile selection by name
Commands that operate on a single profile SHALL accept the profile name as an argument, and
SHALL fail clearly if the named profile does not exist. Commands limited to browsing or
restoring recorded history MAY instead be run without a profile name, in which case the system
SHALL attempt to resolve the profile by walking upward from the current working directory
looking for a `.vara\profile.db` file, the same way version control tools locate their own
metadata directory; this resolution SHALL succeed without requiring `~/.vara/profiles.yml` to
exist or define the resolved profile. If no such file is found before the filesystem root is
reached, the system SHALL report a clear error stating that a profile name is required.

#### Scenario: Unknown profile name
- **WHEN** a user runs a command against a profile name that does not exist in the configuration file
- **THEN** the system reports an error identifying the unknown profile name and performs no action

#### Scenario: Profile name omitted, working directory inside a profile's target root
- **WHEN** a user runs a browsing or restoring command without a profile name, and the current
  working directory is at or below a directory containing a `.vara\profile.db` file
- **THEN** the system resolves that directory as the profile's target root and proceeds without
  needing `~/.vara/profiles.yml` to exist

#### Scenario: Profile name omitted, working directory outside any profile's target root
- **WHEN** a user runs a browsing or restoring command without a profile name, and no `.vara\
  profile.db` file is found by walking upward from the current working directory to the
  filesystem root
- **THEN** the system reports a clear error stating that a profile name is required, and performs
  no action

#### Scenario: Profile name given explicitly while inside a profile's target root
- **WHEN** a user runs a browsing or restoring command with an explicit profile name, regardless
  of whether the current working directory is also inside a profile's target root
- **THEN** the system resolves the profile by the given name via `~/.vara/profiles.yml` (or the
  configured alternate path), rather than by the working directory

### Requirement: Profile structure validation
Each profile SHALL define a name, a target root path, and one or more sources. Each source SHALL define a path and MAY define a recursive flag (default `true`), an exclude list, and glob include/exclude patterns. WHEN a source's exclude list or either glob pattern list is present but is not a list of plain string entries - for example, a scalar value instead of a list, or a list containing a nested mapping or sequence instead of a string - the system SHALL reject the profile with a validation error identifying the profile, the offending field, and the problem, rather than silently treating the malformed field as absent or silently dropping the offending entries.

#### Scenario: Missing required field
- **WHEN** a profile in the configuration file omits its target path or defines zero sources
- **THEN** the system reports a validation error identifying the profile and the missing field, and performs no action for that profile

#### Scenario: Source without recursive flag
- **WHEN** a source entry does not specify a recursive flag
- **THEN** the system treats that source as recursive by default

#### Scenario: Exclude field given as a scalar instead of a list
- **WHEN** a source's `exclude`, `include_globs`, or `exclude_globs` field is present in the configuration file but its value is a scalar (for example, `exclude: "*.tmp"`) rather than a list
- **THEN** the system reports a validation error identifying the profile, the offending source, the field name, and that a list was expected, and performs no action for that profile

#### Scenario: Exclude list contains a non-string entry
- **WHEN** a source's `exclude`, `include_globs`, or `exclude_globs` field is a list but one of its entries is a mapping or a nested sequence rather than a plain string
- **THEN** the system reports a validation error identifying the profile, the offending source, the field name, and the offending entry, and performs no action for that profile

#### Scenario: Well-formed exclude/glob lists accepted
- **WHEN** a source's `exclude`, `include_globs`, and `exclude_globs` fields are each either absent or a list of plain string entries
- **THEN** the system accepts the profile and applies the specified entries without reporting a validation error

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

### Requirement: Duplicate profile names rejected
The system SHALL reject a configuration file that defines two or more profiles with the same name.

#### Scenario: Duplicate names in configuration
- **WHEN** the configuration file lists two profiles with the same name
- **THEN** the system reports a validation error and performs no action against that configuration file

### Requirement: Retention policy configuration
Each profile MAY define a tiered retention policy consisting of the number of daily, weekly, monthly, and yearly snapshots to retain. A profile without a configured retention policy SHALL NOT have retention pruning available until one is configured. Each configured retention count SHALL be a non-negative whole number; the system SHALL reject a retention count that is not a valid non-negative whole number with a validation error identifying the profile, the field, and the offending value, rather than raising an unhandled exception.

#### Scenario: Profile without a retention policy
- **WHEN** a profile's configuration omits a retention policy
- **THEN** commands that depend on retention configuration report that no retention policy is configured for that profile, rather than assuming a default policy

#### Scenario: Retention count is not a valid integer
- **WHEN** a profile's retention policy sets `keep_daily`, `keep_weekly`, `keep_monthly`, or `keep_yearly` to a value that cannot be parsed as a whole number (for example, `abc`)
- **THEN** the system reports a validation error identifying the profile, the field name, and the invalid value, and performs no action for that profile

#### Scenario: Retention count is negative
- **WHEN** a profile's retention policy sets `keep_daily`, `keep_weekly`, `keep_monthly`, or `keep_yearly` to a negative number
- **THEN** the system reports a validation error identifying the profile, the field name, and the invalid value, and performs no action for that profile

### Requirement: Malformed configuration file detection
The system SHALL distinguish a syntactically invalid or structurally malformed profile configuration file from one that legitimately defines zero profiles. When the file's contents cannot be parsed as valid YAML, or parse as valid YAML but are structurally malformed, the system SHALL report a configuration error identifying the file and the problem, rather than allowing the parse failure to propagate as an unhandled exception or silently treating the file as defining zero profiles.

#### Scenario: Configuration file contains a YAML syntax error
- **WHEN** the configuration file's contents cannot be parsed as valid YAML (for example, due to a hand-editing mistake such as inconsistent indentation or an unclosed quote)
- **THEN** the system reports a configuration error identifying the file and stating that its contents could not be parsed as YAML, rather than allowing the raw YAML parser exception to propagate, and performs no action

#### Scenario: Configuration file root is not a mapping
- **WHEN** the configuration file's YAML root node is not a mapping (for example, it is a sequence or a scalar)
- **THEN** the system reports a configuration error identifying the file and stating that its root must be a mapping, and performs no action

#### Scenario: "profiles" key present but not a list
- **WHEN** the configuration file's root mapping defines a `profiles` key whose value is not a sequence (for example, a mapping or a scalar)
- **THEN** the system reports a configuration error identifying the file and stating that `profiles` must be a list, and performs no action

#### Scenario: Empty configuration file treated as zero profiles
- **WHEN** the configuration file contains no YAML documents (an empty file, or a file containing only comments)
- **THEN** the system treats the configuration as defining zero profiles, without reporting an error

#### Scenario: Configuration file without a "profiles" key treated as zero profiles
- **WHEN** the configuration file's root mapping does not contain a `profiles` key
- **THEN** the system treats the configuration as defining zero profiles, without reporting an error

### Requirement: Concurrency configuration
Each profile MAY define a concurrency configuration specifying independent scan and transfer concurrency limits. Each configured concurrency value SHALL be a positive whole number; the system SHALL reject a value that is not a valid positive whole number with a validation error identifying the profile, the field, and the offending value, rather than raising an unhandled exception. A profile without a configured value for a given concurrency setting SHALL use the system's default for that setting.

#### Scenario: Profile without concurrency configuration
- **WHEN** a profile's configuration omits a concurrency section
- **THEN** backup runs for that profile use the system's default scan and transfer concurrency limits

#### Scenario: Scan concurrency configured
- **WHEN** a profile's configuration sets `scan_concurrency` to a positive whole number
- **THEN** backup runs for that profile bound move-detection scan concurrency to that number instead of the default

#### Scenario: Transfer concurrency configured
- **WHEN** a profile's configuration sets `transfer_concurrency` to a positive whole number
- **THEN** backup runs for that profile bound transfer concurrency to that number instead of the default

#### Scenario: Concurrency value is not a valid integer
- **WHEN** a profile's configuration sets `scan_concurrency` or `transfer_concurrency` to a value that cannot be parsed as a whole number (for example, `abc`)
- **THEN** the system reports a validation error identifying the profile, the field name, and the invalid value, and performs no action for that profile

#### Scenario: Concurrency value is zero or negative
- **WHEN** a profile's configuration sets `scan_concurrency` or `transfer_concurrency` to zero or a negative number
- **THEN** the system reports a validation error identifying the profile, the field name, and the invalid value, and performs no action for that profile
