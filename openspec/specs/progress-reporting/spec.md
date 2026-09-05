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

### Requirement: Scan-phase progress indication
Before file transfer begins, while the system is determining the total set of changed files and their total byte size (per the "Upfront work estimation" requirement), the system SHALL display an indeterminate progress indicator, so a user can tell the run is active rather than appearing to hang with no feedback at all.

#### Scenario: Scanning and hashing before transfer begins
- **WHEN** a backup run is enumerating and hashing files to determine the total set of changed files, before any byte transfer has begun
- **THEN** the system displays an indeterminate progress indicator (for example, an indeterminate progress bar) reflecting that work is underway

#### Scenario: Indicator replaced once transfer begins
- **WHEN** file transfer begins after the scan/hash phase completes
- **THEN** the indeterminate progress indicator is replaced by the byte-based progress bar and statistics, per the existing progress-reporting requirements

### Requirement: Throughput and ETA reporting
While transferring file content, the system SHALL display current throughput and an estimated time to completion, updated as the run progresses.

#### Scenario: Long-running transfer
- **WHEN** a backup run is transferring a large volume of changed content
- **THEN** the system periodically updates and displays current throughput and a remaining-time estimate until the run completes

### Requirement: Progress bar occupies a dedicated, width-sized line
While transferring file content, the system SHALL render the progress bar on a line separate from the numeric transfer statistics (bytes transferred/total, throughput, estimated time remaining), and SHALL size the bar to make use of the available terminal width, so the bar's resolution is not constrained by space shared with other text.

#### Scenario: Normal interactive terminal
- **WHEN** a backup run is transferring file content in an interactive terminal of a known width
- **THEN** the progress bar is rendered on its own line, sized to the available terminal width, with the transfer statistics rendered on a separate line below it

#### Scenario: Terminal width cannot be determined
- **WHEN** a backup run is transferring file content but the system cannot determine the terminal's width (for example, because output is redirected)
- **THEN** the system falls back to plain, non-progress-bar output per the `cli-presentation` capability's non-interactive fallback behavior, rather than rendering a bar of an arbitrary or incorrect width

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

### Requirement: Numeric progress fields do not shift layout as values change
The system SHALL render the numeric transfer statistics (bytes transferred/total, throughput, estimated time remaining) using a layout in which a change in any one value's digit count does not alter the horizontal position of the other fields sharing its line.

#### Scenario: A displayed value crosses a digit-count boundary
- **WHEN** a backup run is in progress and a displayed value (for example, throughput) changes from a shorter to a longer representation (such as crossing from single-digit to double-digit units)
- **THEN** the other fields on the same line (for example, bytes transferred or estimated time remaining) do not shift horizontal position as a result

### Requirement: Lightweight operations reported separately from data transfer
The system SHALL distinguish operations that do not transfer file content (for example, moves, hardlinks, and deletions) from byte transfers in its progress and summary reporting, so near-instant operations do not distort throughput or ETA figures. This SHALL include excluding the wall-clock time such operations take from the elapsed-time basis used to compute throughput and ETA, not merely excluding their (zero) byte contribution, so that a batch of lightweight operations preceding byte transfer does not permanently depress the run's reported throughput or inflate its reported ETA.

#### Scenario: Backup run consisting mostly of moved files
- **WHEN** a backup run's changes are dominated by files moved to new paths rather than content changes
- **THEN** the reported throughput and ETA reflect the actual bytes transferred, not the size of the moved files

#### Scenario: Lightweight operations precede byte transfer
- **WHEN** a backup run performs a batch of moves and/or deletions before any file content is transferred
- **THEN** the time spent on that batch is excluded from the elapsed-time basis used to compute throughput and ETA, so that once byte transfer begins, the displayed throughput reflects only the byte-transfer phase's own elapsed time rather than being diluted by the preceding batch's duration

### Requirement: Run summary statistics
Upon completion, the system SHALL report a summary of the backup run: counts of files added, changed, moved, and deleted; total bytes transferred; elapsed time; and any files that failed to back up.

#### Scenario: Run completes with a failed file
- **WHEN** a backup run completes and one file could not be read
- **THEN** the completion summary lists that file as failed alongside the other completion statistics

### Requirement: Backup run outcome reflects failure severity
Upon completion, the system SHALL classify a backup run's outcome as a clean success (no failed files) or a success with partial failures (one or more failed files), and SHALL render the completion summary using the outcome-severity style defined by the `cli-presentation` capability.

#### Scenario: Run completes with no failed files
- **WHEN** a backup run completes and no file failed to back up
- **THEN** the completion summary is rendered in the capability's clean-success style

#### Scenario: Run completes with one or more failed files
- **WHEN** a backup run completes and one or more files failed to back up
- **THEN** the completion summary is rendered in the capability's partial-failure style, distinct from both a clean success and a hard error

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

### Requirement: Count-based progress indication for non-byte-transfer operations
WHEN an operation's work is measured in discrete items rather than bytes transferred (for example, deleting individual unreferenced blobs during pruning), the system SHALL display a count-based progress indicator (items completed out of a known total), distinct from the byte-based progress bar used for file transfers. WHEN the total item count is not yet known (for example, while an operation is still determining what work is eligible), the system SHALL display an indeterminate progress indicator instead, replaced by the count-based indicator once the total is known.

#### Scenario: Total item count known upfront
- **WHEN** an operation has determined the total number of discrete items it will process
- **THEN** the system displays a count-based progress indicator showing items completed against that known total, updating as each item completes

#### Scenario: Total item count not yet known
- **WHEN** an operation is still determining which items are eligible for processing, before a total count is known
- **THEN** the system displays an indeterminate progress indicator, replaced by the count-based indicator once the total becomes known

#### Scenario: Non-interactive or redirected output
- **WHEN** an operation with count-based progress runs with output redirected or otherwise unable to render a live progress indicator
- **THEN** the system falls back to plain, non-progress-bar output, per the `cli-presentation` capability's non-interactive fallback behavior

### Requirement: Aggregate progress across a multi-file directory restore
While a recursive directory restore (per the `snapshot-history` capability) is writing multiple
files, the system SHALL report progress as a single byte-based indicator against the combined
total size of every file to be written in that operation, per the existing byte-based and
upfront-total-known progress requirements, rather than showing a separate progress indicator per
file. Since every file's size is known from the manifest before the restore begins, no separate
scan phase is needed before this indicator can display a known total.

#### Scenario: Restoring a directory with many files
- **WHEN** a recursive directory restore is writing a set of files whose combined transfer takes
  a perceptible amount of time
- **THEN** the system displays one progress indicator that advances continuously as each file is
  written, reflecting bytes written so far against the combined total across all files, rather
  than resetting or restarting for each file

#### Scenario: Restoring a directory whose files are all small
- **WHEN** a recursive directory restore's combined content is small enough that the whole
  operation completes almost immediately
- **THEN** the system completes the restore without the progress indicator producing a noticeable
  or distracting flash on screen

#### Scenario: File removals during a directory restore do not distort progress
- **WHEN** a recursive directory restore both writes files and removes files that did not exist
  at the requested date
- **THEN** the removals, which transfer no bytes, are not counted toward the combined total or
  the bytes-written figure, so they do not distort the displayed percentage, throughput, or ETA

#### Scenario: Non-interactive or redirected output
- **WHEN** a recursive directory restore runs with output redirected or otherwise unable to
  render a live progress bar
- **THEN** the system falls back to plain, non-progress-bar output, per the `cli-presentation`
  capability's non-interactive fallback behavior
