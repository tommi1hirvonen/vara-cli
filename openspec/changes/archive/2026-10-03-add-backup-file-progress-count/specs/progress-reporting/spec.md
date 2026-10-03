## ADDED Requirements

### Requirement: File-transfer count progress
During a backup run, the system SHALL report successfully transferred files against the total planned files whose content will be transferred. The total SHALL include planned additions and changes only; metadata-only operations such as moves, links, and deletions SHALL NOT be included. A file SHALL count as transferred only after its content transfer and recording succeed.

#### Scenario: Mixed-size files are transferred
- **WHEN** a backup run transfers a plan containing multiple additions and changes
- **THEN** the progress statistics show the number of successfully transferred files out of the total planned additions and changes, independently of byte progress

#### Scenario: A content transfer fails
- **WHEN** a planned content transfer fails
- **THEN** the failed file remains in the total and is not included in the successfully transferred file count

#### Scenario: Plan contains only metadata operations
- **WHEN** a backup run has moves, links, or deletions but no additions or changes
- **THEN** the file-transfer count reports zero transferred out of zero planned files

### Requirement: Backup transfer statistics use two aligned interactive rows
While a backup run transfers file content in an interactive terminal, the system SHALL display bytes transferred out of total bytes and files transferred out of total files on one statistics row, with throughput and estimated time remaining on a second row. Each field SHALL retain a stable horizontal position as its value changes. The statistics rows SHALL remain below the dedicated progress-bar line.

#### Scenario: Normal interactive transfer
- **WHEN** a backup run transfers content in an interactive terminal
- **THEN** the statistics below the progress bar show bytes and files on the first row and throughput and estimated time remaining on the second row

#### Scenario: A displayed value changes digit count
- **WHEN** any displayed statistic changes to a value with a different number of digits
- **THEN** the other fields retain their horizontal positions within their respective rows

#### Scenario: Non-interactive transfer output
- **WHEN** a backup run transfers content with output redirected or otherwise unable to render an interactive progress display
- **THEN** the plain-text progress output includes successfully transferred files out of total planned files alongside the existing byte, percentage, throughput, and ETA values
