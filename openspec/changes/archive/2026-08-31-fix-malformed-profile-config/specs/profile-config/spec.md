## ADDED Requirements

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
