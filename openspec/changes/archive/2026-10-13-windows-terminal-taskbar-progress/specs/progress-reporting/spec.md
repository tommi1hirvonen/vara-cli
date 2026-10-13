## ADDED Requirements

### Requirement: Windows Terminal progress indicator
When a user runs a progress-reporting command with human-readable output in Windows Terminal, the system SHALL reflect the command's existing progress in Windows Terminal's tab and taskbar indicator. This SHALL cover `vara backup` (including `--dry-run`), `vara restore` (including recursive restore), `vara check`, and `vara prune`, without changing their existing terminal progress displays.

#### Scenario: Indeterminate phase
- **WHEN** a covered command reports a phase whose total work is not yet known
- **THEN** Windows Terminal displays an indeterminate progress indicator until the command reports determinate progress or the operation ends

#### Scenario: Determinate phase
- **WHEN** a covered command reports a known completion percentage
- **THEN** Windows Terminal displays determinate progress corresponding to that percentage, bounded from 0 through 100 percent

#### Scenario: Command completes or stops
- **WHEN** a covered command completes, is cancelled, or fails
- **THEN** the system clears its Windows Terminal progress indicator so no stale progress remains

### Requirement: Terminal progress compatibility
The system SHALL emit Windows Terminal progress control sequences only when output is connected to Windows Terminal and the command is producing human-readable progress. It SHALL NOT emit those sequences to redirected output or machine-readable JSON output, and SHALL preserve existing command output behavior in other terminals.

#### Scenario: Progress in a supported terminal
- **WHEN** a covered command runs with human-readable progress in Windows Terminal
- **THEN** progress control sequences are sent without adding visible text to the command's output

#### Scenario: Redirected or machine-readable output
- **WHEN** a covered command runs with output redirected or with machine-readable JSON output selected
- **THEN** no Windows Terminal progress control sequences are emitted

#### Scenario: Another terminal host
- **WHEN** a covered command runs in a terminal other than Windows Terminal
- **THEN** the existing progress display and command output remain unchanged
