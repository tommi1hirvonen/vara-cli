## ADDED Requirements

### Requirement: Active file transfers listed in progress display
While a backup run is transferring file content in an interactive terminal, the system SHALL display the absolute paths of files currently being transferred below the existing statistics rows, truncated to a maximum of 48 characters using the same path-truncation rule as the restore task label (drop leading parent directory segments, prefix with `...`). If fewer characters are needed to display the absolute path, the absolute path SHALL be shown unmodified. If more than three files are being transferred concurrently, the system SHALL show the first three paths followed by a `+ N more` line indicating how many additional transfers are in flight. If fewer than three files are in flight, only those paths are shown, with no placeholder lines for the absent slots.

#### Scenario: One file being transferred
- **WHEN** a backup run is transferring exactly one file at a time
- **THEN** the progress display shows that file's path (truncated if necessary) below the statistics rows, with no additional lines

#### Scenario: Three files being transferred concurrently
- **WHEN** a backup run is transferring exactly three files concurrently
- **THEN** the progress display shows each file's path (truncated if necessary) on its own line below the statistics rows, with no `+ N more` line

#### Scenario: More than three files being transferred concurrently
- **WHEN** a backup run is transferring more than three files concurrently
- **THEN** the progress display shows the first three paths and a `+ N more` line below the statistics rows, where N is the number of additional in-flight transfers beyond the three shown

#### Scenario: Short absolute path fits within budget
- **WHEN** a file being transferred has an absolute path of 48 characters or fewer
- **THEN** the path is displayed unmodified in the active-transfers list

#### Scenario: Long absolute path exceeds budget
- **WHEN** a file being transferred has an absolute path longer than 48 characters
- **THEN** the path is shortened by dropping leading parent directory segments and prefixing the remainder with `...`, so the displayed label fits within 48 characters

#### Scenario: No files currently being transferred
- **WHEN** a backup run is in the transfer phase but no file transfers are currently in flight (for example, between the completion of one file and the start of the next, or when all transfers have finished)
- **THEN** no active-transfers lines are shown below the statistics rows
