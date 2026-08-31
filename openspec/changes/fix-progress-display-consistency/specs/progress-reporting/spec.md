## ADDED Requirements

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
