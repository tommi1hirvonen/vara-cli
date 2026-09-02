## MODIFIED Requirements

### Requirement: Scan-phase progress indication
Before file transfer begins, while the system is determining the total set of changed files and their total byte size (per the "Upfront work estimation" requirement), the system SHALL display an indeterminate progress indicator, so a user can tell the run is active rather than appearing to hang with no feedback at all.

#### Scenario: Scanning and hashing before transfer begins
- **WHEN** a backup run is enumerating and hashing files to determine the total set of changed files, before any byte transfer has begun
- **THEN** the system displays an indeterminate progress indicator (for example, an indeterminate progress bar) reflecting that work is underway

#### Scenario: Indicator replaced once transfer begins
- **WHEN** file transfer begins after the scan/hash phase completes
- **THEN** the indeterminate progress indicator is replaced by the byte-based progress bar and statistics, per the existing progress-reporting requirements

## ADDED Requirements

### Requirement: Progress bar reflects run outcome severity
While a backup run's scan phase is underway, the system SHALL render its indeterminate progress indicator using the `cli-presentation` capability's neutral pastel color. While the transfer phase is underway and the run has not yet reached a final outcome, the system SHALL render the byte-based progress bar's fill using the capability's partial-failure pastel color (amber), so an in-progress bar is visually distinct from one that has reached a final outcome. Once the run reaches a final outcome, the system SHALL recolor the entire bar - whichever of the scan indicator or transfer bar was most recently displayed - to that outcome's own severity color from the same palette: the success color (green) for a clean completion, the partial-failure color (amber) for a completion with one or more failed files, or the error color (red) for a run that terminates with a hard error before completing. This final recoloring SHALL reflect the run's actual outcome regardless of the byte-based percentage reached at that moment.

#### Scenario: Scan phase in progress
- **WHEN** a backup run's scan phase is underway
- **THEN** the indeterminate progress indicator renders in the capability's neutral color

#### Scenario: Transfer phase in progress
- **WHEN** a backup run's transfer phase is underway and the run has not yet reached a final outcome
- **THEN** the progress bar's fill renders in the capability's partial-failure (amber) color

#### Scenario: Run completes with no failed files
- **WHEN** a backup run completes and no file failed to back up
- **THEN** the progress bar renders entirely in the capability's success (green) color

#### Scenario: Run completes with one or more failed files
- **WHEN** a backup run completes and one or more files failed to back up
- **THEN** the progress bar renders entirely in the capability's partial-failure (amber) color, even if the byte-based percentage transferred did not reach 100% at completion

#### Scenario: Run terminates with a hard error
- **WHEN** a backup run raises an unhandled exception before completing, during either the scan or transfer phase
- **THEN** the bar most recently displayed renders entirely in the capability's error (red) color before the error is reported
