## ADDED Requirements

### Requirement: Machine-readable JSON output for the backup command
The system SHALL provide a `--json` option to `vara backup`, usable together with or without `--dry-run`, that prints a single machine-readable JSON object representing the run's (or dry run's) outcome to stdout, instead of the human-oriented colored summary. WHEN `--json` is given, the system SHALL suppress the interactive/plain-text progress display, so stdout carries only the final JSON object and is safe to pipe into a JSON parser. This option is scoped to the `backup` command only; no other command is required to support it.

#### Scenario: JSON output for a completed real run
- **WHEN** a user runs `vara backup --json` for a profile
- **THEN** the system prints a single JSON object to stdout summarizing the run's added/changed/moved/deleted/failed counts, bytes transferred, and whether it was cancelled, and prints no progress display or other non-JSON content to stdout

#### Scenario: JSON output for a dry run
- **WHEN** a user runs `vara backup --dry-run --json` for a profile
- **THEN** the system prints a single JSON object to stdout summarizing the planned added/changed/moved/deleted counts and total bytes that would be transferred, distinguishing it as a dry run, and prints no progress display or other non-JSON content to stdout

#### Scenario: Non-JSON output unaffected
- **WHEN** a user runs `vara backup` (or `vara backup --dry-run`) without `--json`
- **THEN** the system's output is unchanged from its behavior before this requirement was added
