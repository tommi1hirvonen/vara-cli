# cli-presentation Specification

## Purpose

Defines the terminal presentation conventions shared by every Vara command - how outcome severity is visually distinguished, and how output degrades gracefully when the terminal cannot support rich formatting.

## Requirements

### Requirement: Outcome severity is visually distinct
The system SHALL render any reported command outcome - a full success, a success with partial failures, or a hard error - in a visually distinct style per its severity, consistently across every command, so a user can tell an outcome's severity without reading its text closely.

#### Scenario: Command completes with no failures
- **WHEN** a command completes with no failures of any kind
- **THEN** the system reports the outcome in a bold, success-colored style (green)

#### Scenario: Command completes with partial failures
- **WHEN** a command completes overall but one or more individual items (for example, files) failed
- **THEN** the system reports the outcome in a style distinct from both a full success and a hard error (amber/yellow)

#### Scenario: Command reports a hard error
- **WHEN** any command reports a hard error that prevented it from completing
- **THEN** the system reports the error in a distinct error style (red), regardless of which command produced the error

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
