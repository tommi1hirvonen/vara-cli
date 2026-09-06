## MODIFIED Requirements

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
