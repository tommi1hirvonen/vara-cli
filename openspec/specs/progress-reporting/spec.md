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

### Requirement: Progress display never regresses to a stale value
The system SHALL ensure that, once a progress update reflecting a given cumulative bytes-transferred value has been displayed to the user for a run, no subsequently displayed update for that run SHALL show a lower cumulative bytes-transferred value.

#### Scenario: Out-of-order progress delivery
- **WHEN** two progress updates for the same run are delivered out of the order in which the underlying transfers completed (for example, because concurrent worker threads deliver updates independently)
- **THEN** the displayed progress line reflects only the highest cumulative bytes-transferred value received so far, and never regresses to an earlier, lower value

### Requirement: Progress display updates are rate-limited
The system SHALL limit how frequently the progress line is redrawn on screen, independent of how many individual file-transfer completions occur, so that displayed values remain readable during runs with many small files. The system SHALL redraw the progress line immediately when file transfer begins and when the run completes, regardless of the rate limit.

#### Scenario: Many small files transferred rapidly
- **WHEN** a backup run transfers a large number of small files in quick succession
- **THEN** the progress line is redrawn at a bounded, readable rate rather than once per completed file, while still reflecting the most recent cumulative progress at each redraw

#### Scenario: Progress shown at start and completion regardless of rate limit
- **WHEN** a backup run begins transferring files or completes its final transfer
- **THEN** the progress display is updated immediately for that event, without waiting for the rate-limit interval to elapse

### Requirement: Incremental progress during in-flight file transfers
The system SHALL report transfer progress incrementally as a file's content is copied, rather than only once that file's transfer has fully completed, so that the displayed bytes-transferred, percentage, throughput, and ETA continue to advance while a single large file is still being transferred. This SHALL apply both to reading a source file's content into the store and to placing stored content at a mirror path via a real file copy (used when the target filesystem does not support hardlinks, or when a blob has reached its hard-link limit). When placing content at every mirror path is known in advance to require a real copy (the target filesystem does not support hardlinks at all), the system SHALL account for that additional copy pass in the total-bytes-to-transfer figure used for the live percentage and ETA, so the displayed percentage remains meaningful rather than exceeding 100% over the course of the run.

#### Scenario: Single large file transfer in progress
- **WHEN** a backup run is transferring a file large enough that its transfer takes a perceptible amount of time
- **THEN** the displayed bytes-transferred, percentage, throughput, and ETA continue to advance while that file's transfer is still in progress, rather than remaining unchanged until the file completes

#### Scenario: Two large files transferred in sequence
- **WHEN** a backup run transfers two large files one after another
- **THEN** the displayed progress advances continuously throughout both files' transfers, rather than jumping only when each file completes

#### Scenario: Large file placed at its mirror path via a real copy
- **WHEN** a backup run places a large stored blob at a mirror path using a real file copy rather than a hardlink (because the target filesystem does not support hardlinks, or the blob has reached its hard-link limit)
- **THEN** the displayed progress continues to advance while that copy is still in progress, rather than remaining unchanged until it completes

#### Scenario: Target filesystem without hardlink support does not overshoot 100%
- **WHEN** a backup run's target filesystem does not support hardlinks, so every transferred file's content is copied once into the local store and once more onto the mirror
- **THEN** the displayed percentage reflects both copy passes' bytes against a total that accounts for both, so it does not exceed 100% over the course of the run

### Requirement: Progress heartbeat during stalled transfers
WHEN no new byte-progress event has been reported for longer than the display's normal redraw interval while a run is in progress, the system SHALL still periodically redraw the progress display using the most recently reported cumulative bytes-transferred value together with the current elapsed time, so the displayed throughput and estimated time to completion continue to reflect real elapsed time rather than remaining frozen at values computed before the stall began.

#### Scenario: Transfer stalls with no new bytes reported
- **WHEN** a backup run's file transfer stalls (for example, a slow or hung disk read) for longer than the display's normal redraw interval, and no new cumulative bytes-transferred value is reported during that time
- **THEN** the displayed throughput decreases and the estimated time to completion increases to reflect the growing elapsed time, even though the displayed cumulative bytes-transferred value has not changed

#### Scenario: Transfer resumes after a stall
- **WHEN** a stalled transfer resumes and new byte-progress events are reported again
- **THEN** the displayed progress continues to advance normally from where it left off, reflecting the newly reported bytes

#### Scenario: Heartbeat stops once the run completes
- **WHEN** a backup run completes
- **THEN** the system stops redrawing the progress display on a heartbeat basis, leaving only the run's final completion state displayed
