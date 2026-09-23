## ADDED Requirements

### Requirement: Backup command accepts profile as a positional argument
The system SHALL provide a `vara backup <profile>` command that requires the profile name as a positional argument. The command SHALL NOT accept a `--profile` option. Supplying `--profile` or omitting the profile positional argument SHALL be rejected as a syntax error.

#### Scenario: Running backup with positional profile argument
- **WHEN** a user runs `vara backup <profile>` with a valid profile name as a positional argument
- **THEN** the system resolves the profile by name and executes the incremental backup pipeline

#### Scenario: Running backup without a positional profile argument
- **WHEN** a user runs `vara backup` without specifying a profile argument
- **THEN** the system rejects the invocation with an error indicating the required profile argument is missing

#### Scenario: Running backup with legacy --profile option
- **WHEN** a user runs `vara backup --profile <name>`
- **THEN** the system rejects the option as unrecognized
