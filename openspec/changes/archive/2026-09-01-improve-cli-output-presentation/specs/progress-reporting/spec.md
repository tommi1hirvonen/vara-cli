## ADDED Requirements

### Requirement: Progress bar occupies a dedicated, width-sized line
While transferring file content, the system SHALL render the progress bar on a line separate from the numeric transfer statistics (bytes transferred/total, throughput, estimated time remaining), and SHALL size the bar to make use of the available terminal width, so the bar's resolution is not constrained by space shared with other text.

#### Scenario: Normal interactive terminal
- **WHEN** a backup run is transferring file content in an interactive terminal of a known width
- **THEN** the progress bar is rendered on its own line, sized to the available terminal width, with the transfer statistics rendered on a separate line below it

#### Scenario: Terminal width cannot be determined
- **WHEN** a backup run is transferring file content but the system cannot determine the terminal's width (for example, because output is redirected)
- **THEN** the system falls back to plain, non-progress-bar output per the `cli-presentation` capability's non-interactive fallback behavior, rather than rendering a bar of an arbitrary or incorrect width

### Requirement: Numeric progress fields do not shift layout as values change
The system SHALL render the numeric transfer statistics (bytes transferred/total, throughput, estimated time remaining) using a layout in which a change in any one value's digit count does not alter the horizontal position of the other fields sharing its line.

#### Scenario: A displayed value crosses a digit-count boundary
- **WHEN** a backup run is in progress and a displayed value (for example, throughput) changes from a shorter to a longer representation (such as crossing from single-digit to double-digit units)
- **THEN** the other fields on the same line (for example, bytes transferred or estimated time remaining) do not shift horizontal position as a result

### Requirement: Backup run outcome reflects failure severity
Upon completion, the system SHALL classify a backup run's outcome as a clean success (no failed files) or a success with partial failures (one or more failed files), and SHALL render the completion summary using the outcome-severity style defined by the `cli-presentation` capability.

#### Scenario: Run completes with no failed files
- **WHEN** a backup run completes and no file failed to back up
- **THEN** the completion summary is rendered in the capability's clean-success style

#### Scenario: Run completes with one or more failed files
- **WHEN** a backup run completes and one or more files failed to back up
- **THEN** the completion summary is rendered in the capability's partial-failure style, distinct from both a clean success and a hard error
