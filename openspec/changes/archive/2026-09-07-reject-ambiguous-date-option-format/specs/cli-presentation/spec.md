## MODIFIED Requirements

### Requirement: Invalid date/time option input produces a friendly error
WHEN a command option that accepts a date/time value (`--at`, `--since`, `--left-at`, or `--right-at`) is given a value that cannot be parsed as an unambiguous date/time, the system SHALL report a friendly, actionable error identifying the option and the invalid value, rather than allowing the parse failure to propagate as an unclassified exception. A value SHALL be considered parseable only if its date portion is unambiguous (ISO 8601, e.g. `2025-01-02`, optionally followed by a time-of-day and/or a UTC offset); a locale-dependent, slash-separated date (for example, `01/02/2025`) SHALL be treated the same as any other unparseable value, since it cannot be resolved to a single date without guessing an unstated interpretation.

#### Scenario: Unparseable value given to a date/time option
- **WHEN** a user runs a command with a date/time option set to a value that cannot be parsed (for example, `--at yesterday`)
- **THEN** the system reports an error identifying the option and the invalid value, and does not proceed with the command's action

#### Scenario: Ambiguous slash-separated date given to a date/time option
- **WHEN** a user runs a command with a date/time option set to a slash-separated numeric date (for example, `--at 01/02/2025`)
- **THEN** the system reports an error identifying the option and the invalid value, the same as for any other unparseable value, rather than silently resolving it under one particular interpretation

#### Scenario: Valid value still parses as before
- **WHEN** a user runs a command with a date/time option set to an unambiguous (ISO 8601) value that can be parsed
- **THEN** the system proceeds exactly as before this requirement was added, with no change to the resolved date/time
