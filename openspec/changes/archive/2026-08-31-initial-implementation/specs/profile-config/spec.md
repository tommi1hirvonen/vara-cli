## Purpose

Defines how Vara locates, loads, and validates the user's backup profile configuration - profiles, their sources, target, and retention settings - so that every other capability has a validated profile definition to act on.

## ADDED Requirements

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
Each profile MAY define a tiered retention policy consisting of the number of daily, weekly, monthly, and yearly snapshots to retain. A profile without a configured retention policy SHALL NOT have retention pruning available until one is configured.

#### Scenario: Profile without a retention policy
- **WHEN** a profile's configuration omits a retention policy
- **THEN** commands that depend on retention configuration report that no retention policy is configured for that profile, rather than assuming a default policy
