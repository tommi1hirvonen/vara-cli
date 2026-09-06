## ADDED Requirements

### Requirement: Invalid date/time option input produces a friendly error
WHEN a command option that accepts a date/time value (`--at`, `--since`, `--left-at`, or `--right-at`) is given a value that cannot be parsed as a date/time, the system SHALL report a friendly, actionable error identifying the option and the invalid value, rather than allowing the parse failure to propagate as an unclassified exception.

#### Scenario: Unparseable value given to a date/time option
- **WHEN** a user runs a command with a date/time option set to a value that cannot be parsed (for example, `--at yesterday`)
- **THEN** the system reports an error identifying the option and the invalid value, and does not proceed with the command's action

#### Scenario: Valid value still parses as before
- **WHEN** a user runs a command with a date/time option set to a value that can be parsed
- **THEN** the system proceeds exactly as before this requirement was added, with no change to the resolved date/time

### Requirement: Date/time options document an example value in their help text
The system SHALL include an example date/time value in the description shown by `-h`/`--help` for every command option that accepts a date/time value, so a user can see an accepted format before providing one.

#### Scenario: Viewing help for a command with a date/time option
- **WHEN** a user requests help for a command that accepts a date/time option
- **THEN** the option's description includes an example date/time value in the accepted format
