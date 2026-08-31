# progress-reporting Specification

## Purpose

Defines how Vara communicates backup progress and outcome to the user in the terminal, so the reported completion percentage, throughput, and time estimates during a run can be trusted.

## Requirements

### Requirement: Byte-based progress
During a backup run, the system SHALL report progress based on the total bytes of content to be transferred and the bytes transferred so far, not merely file or directory counts.

#### Scenario: Mixed file sizes
- **WHEN** a backup run includes both many small files and a few very large files
- **THEN** the reported progress percentage reflects the proportion of bytes transferred, not the proportion of files processed

### Requirement: Upfront work estimation
Before transferring any file content, the system SHALL determine the total set of changed files and their total byte size, so that progress and estimated time to completion can be reported from the start of execution.

#### Scenario: Progress shown at start of transfer
- **WHEN** file transfer begins during a backup run
- **THEN** the system immediately displays a total-bytes-to-transfer figure and an initial time estimate, without needing to complete the transfer first

### Requirement: Throughput and ETA reporting
While transferring file content, the system SHALL display current throughput and an estimated time to completion, updated as the run progresses.

#### Scenario: Long-running transfer
- **WHEN** a backup run is transferring a large volume of changed content
- **THEN** the system periodically updates and displays current throughput and a remaining-time estimate until the run completes

### Requirement: Lightweight operations reported separately from data transfer
The system SHALL distinguish operations that do not transfer file content (for example, moves, hardlinks, and deletions) from byte transfers in its progress and summary reporting, so near-instant operations do not distort throughput or ETA figures.

#### Scenario: Backup run consisting mostly of moved files
- **WHEN** a backup run's changes are dominated by files moved to new paths rather than content changes
- **THEN** the reported throughput and ETA reflect the actual bytes transferred, not the size of the moved files

### Requirement: Run summary statistics
Upon completion, the system SHALL report a summary of the backup run: counts of files added, changed, moved, and deleted; total bytes transferred; elapsed time; and any files that failed to back up.

#### Scenario: Run completes with a failed file
- **WHEN** a backup run completes and one file could not be read
- **THEN** the completion summary lists that file as failed alongside the other completion statistics
