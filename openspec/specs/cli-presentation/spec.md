# cli-presentation Specification

## Purpose

Defines the terminal presentation conventions shared by every Vara command - how outcome severity is visually distinguished, and how output degrades gracefully when the terminal cannot support rich formatting.

## Requirements

### Requirement: Outcome severity is visually distinct
The system SHALL render any reported command outcome - a full success, a success with partial failures, or a hard error - in a visually distinct style per its severity, consistently across every command, so a user can tell an outcome's severity without reading its text closely. WHEN an outcome's report includes supporting detail beyond the outcome itself (for example, per-item counts, or a multi-line summary), the severity style SHALL apply to the outcome indicator only, and the supporting detail SHALL render in the default, unstyled text, so the report is not dominated by a single color.

#### Scenario: Command completes with no failures
- **WHEN** a command completes with no failures of any kind
- **THEN** the system reports the outcome in a bold, success-colored style (green)

#### Scenario: Command completes with partial failures
- **WHEN** a command completes overall but one or more individual items (for example, files) failed
- **THEN** the system reports the outcome in a style distinct from both a full success and a hard error (amber/yellow)

#### Scenario: Command reports a hard error
- **WHEN** any command reports a hard error that prevented it from completing
- **THEN** the system reports the error in a distinct error style (red), regardless of which command produced the error

#### Scenario: Outcome report includes multi-line supporting detail
- **WHEN** a command's reported outcome includes supporting detail beyond the outcome indicator itself (for example, a backup run's per-category file counts and byte totals)
- **THEN** only the outcome indicator is rendered in the severity-appropriate style, and the supporting detail renders in the default, unstyled text

### Requirement: Outcome severity determines process exit code
The system SHALL map each of the three outcome severities already defined by the
"Outcome severity is visually distinct" requirement (full success, success with
partial failures, hard error) to a distinct process exit code, consistently across
every command that can produce that severity, so a caller (for example, a
Task Scheduler job or a script) can distinguish the three outcomes without parsing
console output. A full success SHALL exit `0`. A hard error SHALL exit `1`. A success
with partial failures SHALL exit a distinct non-zero code from a hard error, so a
caller does not need to treat "some files were skipped" the same as "the run did not
complete at all".

#### Scenario: Command completes with no failures
- **WHEN** a command completes with no failures of any kind
- **THEN** the process exits with code `0`

#### Scenario: Command completes with partial failures
- **WHEN** a command completes overall but one or more individual items (for example,
  files) failed
- **THEN** the process exits with a non-zero code distinct from the hard-error exit
  code

#### Scenario: Command reports a hard error
- **WHEN** any command reports a hard error that prevented it from completing
- **THEN** the process exits with code `1`, regardless of which command produced the
  error

### Requirement: Non-interactive or color-incapable output falls back to plain text
WHEN the system's standard output is redirected or piped, connected to a terminal that does not support color, or the user has set the `NO_COLOR` environment convention, the system SHALL render plain, uncolored text and SHALL NOT attempt any live, in-place-redrawn display, rather than emitting raw escape sequences or corrupting the output stream.

#### Scenario: Output redirected to a file or pipe
- **WHEN** a command's standard output is redirected to a file or piped to another process
- **THEN** the system renders plain, uncolored text without attempting to redraw any line in place

#### Scenario: NO_COLOR is set
- **WHEN** the `NO_COLOR` environment variable is set, regardless of whether output is redirected
- **THEN** the system renders plain, uncolored text while still using any applicable layout (for example, tables) without color styling

#### Scenario: Terminal does not support color
- **WHEN** the system detects that the connected terminal does not support colored output
- **THEN** the system renders plain, uncolored text rather than emitting color escape sequences the terminal cannot interpret

### Requirement: Unclassified exceptions are rendered in the hard-error style
WHEN any command raises an exception that is not recognized as a known error with a friendly message, the system SHALL render that exception - including a formatted stack trace - to standard error using the hard-error severity style, rather than allowing it to propagate unhandled and produce a raw, unstyled stack trace.

#### Scenario: An unrecognized exception is raised
- **WHEN** a command raises an exception that the system does not recognize as one of its known, friendly-message error conditions
- **THEN** the system renders the exception's details, including its stack trace, to standard error in the hard-error style, and the command exits with a non-zero status

#### Scenario: A recognized exception is still handled as before
- **WHEN** a command raises an exception the system does recognize as a known error condition
- **THEN** the system continues to render only that exception's friendly message in the hard-error style, without a stack trace, exactly as before this capability's catch-all was added

### Requirement: Bordered widgets use a consistent rounded-corner style
The system SHALL render every bordered widget it displays - including tables and any future panel-style widget - with rounded corners, consistently across every command, so the terminal output presents one coherent visual identity rather than a mix of border styles.

#### Scenario: A command renders a table
- **WHEN** any command renders tabular output (for example, a listing of snapshots or file versions)
- **THEN** the table's border uses the rounded-corner style

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

### Requirement: Date/time options document an example value in their help text
The system SHALL include an example date/time value in the description shown by `-h`/`--help` for every command option that accepts a date/time value, so a user can see an accepted format before providing one.

#### Scenario: Viewing help for a command with a date/time option
- **WHEN** a user requests help for a command that accepts a date/time option
- **THEN** the option's description includes an example date/time value in the accepted format

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
