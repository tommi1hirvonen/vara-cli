## MODIFIED Requirements

### Requirement: Progress display updates are rate-limited
The system SHALL limit how frequently numeric progress statistics (including bytes transferred, completed-file count, throughput, and ETA) are redrawn, independent of how many individual file-transfer completions occur, so that displayed values remain readable during runs with many small files. Intermediate file starts and completions SHALL NOT bypass this rate limit for numeric statistics. The initial transfer update and the final run update SHALL still be displayed immediately. Changes to the active-transfer list SHALL be refreshed independently of numeric statistics: file lifecycle events SHALL request a coalesced refresh that uses the latest known in-flight paths without advancing the numeric statistics. A refresh SHALL NOT display a path known to have completed before the refresh state is captured. If a transfer starts and completes before an active-path refresh is rendered, the system MAY omit that path rather than display it as active after completion. Coalescing SHALL prevent an unbounded backlog of refresh requests, and each rendered active-path list SHALL reflect the latest available in-flight state at the time it is captured.

#### Scenario: Many small files transferred rapidly
- **WHEN** a backup run transfers a large number of small files in quick succession
- **THEN** numeric progress statistics are redrawn at the existing bounded, readable rate, while active-path refresh requests are coalesced independently and each active-path redraw uses the latest available in-flight state

#### Scenario: Active-path change does not advance numeric statistics
- **WHEN** a file starts or finishes between two admitted numeric-statistics updates
- **THEN** the active-transfer list can be refreshed independently while the displayed bytes, completed-file count, throughput, and ETA remain unchanged until the next admitted numeric update

#### Scenario: Completed file is not shown as active
- **WHEN** a transfer finishes before the active-path state for a pending refresh is captured
- **THEN** the refreshed list does not include that file, even if its earlier start event was also pending

#### Scenario: Transfer completes before a truthful refresh
- **WHEN** a file starts and completes before an active-path refresh can be rendered
- **THEN** the file may not appear in the active-transfer list rather than being shown after it has completed

#### Scenario: Progress shown at start and completion regardless of rate limit
- **WHEN** a backup begins transferring files or the run completes
- **THEN** the initial transfer statistics and final run statistics are displayed immediately, without waiting for the numeric-statistics rate-limit interval to elapse
