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
Commands that operate on a single profile SHALL require the user to specify the profile name as an argument, and SHALL fail clearly if the named profile does not exist.

#### Scenario: Unknown profile name
- **WHEN** a user runs a command against a profile name that does not exist in the configuration file
- **THEN** the system reports an error identifying the unknown profile name and performs no action

### Requirement: Profile structure validation
Each profile SHALL define a name, a target root path, and one or more sources. Each source SHALL define a path and MAY define a recursive flag (default `true`), an exclude list, and glob include/exclude patterns.

#### Scenario: Missing required field
- **WHEN** a profile in the configuration file omits its target path or defines zero sources
- **THEN** the system reports a validation error identifying the profile and the missing field, and performs no action for that profile

#### Scenario: Source without recursive flag
- **WHEN** a source entry does not specify a recursive flag
- **THEN** the system treats that source as recursive by default

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
The system SHALL distinguish a structurally malformed profile configuration file from one that legitimately defines zero profiles. When the file is structurally malformed, the system SHALL report a configuration error identifying the file and the structural problem, rather than silently treating it as defining zero profiles.

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
