## ADDED Requirements

### Requirement: Progress bar row includes a leading phase description
While a backup run's scan or transfer phase is underway, the system SHALL render a phase description to the left of the progress bar on the same line, naming the phase currently underway, instead of rendering that description as trailing text to the right of the bar. The transfer phase's row SHALL continue to render its byte-based percentage to the right of the bar. The scan phase's row, which has no percentage to show (its indicator being indeterminate), SHALL render a placeholder of the same width in that position, so the bar itself renders at the same length in both the scan and transfer phases.

#### Scenario: Scan phase in progress
- **WHEN** a backup run's scan phase is underway
- **THEN** a phase description (for example, "Scanning files...") is rendered to the left of the indeterminate progress indicator, with a placeholder of fixed width rendered to its right

#### Scenario: Transfer phase in progress
- **WHEN** a backup run's transfer phase is underway
- **THEN** a phase description (for example, "Backing up...") is rendered to the left of the progress bar, with the byte-based percentage rendered to its right, in the same position the scan phase's placeholder occupies

#### Scenario: Bar length is consistent across phases
- **WHEN** a backup run transitions from its scan phase to its transfer phase
- **THEN** the progress bar renders at the same length in both phases' rows, despite the scan phase's placeholder and the transfer phase's percentage potentially differing in the number of characters they occupy
