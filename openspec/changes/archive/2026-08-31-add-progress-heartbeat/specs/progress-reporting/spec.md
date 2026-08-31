## ADDED Requirements

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
