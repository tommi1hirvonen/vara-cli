## ADDED Requirements

### Requirement: Dry-run scan progress indication
When a user runs `vara backup --dry-run` with human-readable output in a terminal that supports live progress, the system SHALL display an indeterminate scan-phase progress indicator while planning is underway, then stop the indicator and display the dry-run summary. The system SHALL NOT display progress when `--json` is selected, and SHALL preserve the existing non-interactive fallback when live progress is unavailable.

#### Scenario: Dry-run planning in a live terminal
- **WHEN** a user runs `vara backup --dry-run` without `--json` in a terminal that supports live progress
- **THEN** an indeterminate scan-phase indicator is displayed while the plan is being calculated, and the indicator stops before the dry-run summary is displayed

#### Scenario: Dry-run JSON output
- **WHEN** a user runs `vara backup --dry-run --json`
- **THEN** no progress indicator is displayed, and stdout contains only the final JSON object

#### Scenario: Dry-run with redirected output
- **WHEN** a user runs `vara backup --dry-run` with output redirected or otherwise unable to render live progress
- **THEN** the command does not attempt to render a live progress indicator and follows its existing non-interactive output behavior
