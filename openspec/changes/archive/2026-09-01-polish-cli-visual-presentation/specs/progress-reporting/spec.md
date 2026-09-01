## ADDED Requirements

### Requirement: Scan-phase progress indication
Before file transfer begins, while the system is determining the total set of changed files and their total byte size (per the "Upfront work estimation" requirement), the system SHALL display an indeterminate progress indicator, so a user can tell the run is active rather than appearing to hang with no feedback at all.

#### Scenario: Scanning and hashing before transfer begins
- **WHEN** a backup run is enumerating and hashing files to determine the total set of changed files, before any byte transfer has begun
- **THEN** the system displays an indeterminate progress indicator (for example, a spinner) reflecting that work is underway

#### Scenario: Indicator replaced once transfer begins
- **WHEN** file transfer begins after the scan/hash phase completes
- **THEN** the indeterminate progress indicator is replaced by the byte-based progress bar and statistics, per the existing progress-reporting requirements
