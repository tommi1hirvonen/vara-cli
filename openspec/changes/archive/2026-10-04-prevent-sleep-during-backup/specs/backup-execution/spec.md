## ADDED Requirements

### Requirement: Automatic system sleep is prevented during a real backup
On Windows, the system SHALL request prevention of automatic system sleep for the duration of a real backup run. It SHALL release that request when the run completes, is cancelled, or fails. The request SHALL NOT require the display to remain on.

#### Scenario: Successful real backup
- **WHEN** a real backup run starts on Windows
- **THEN** the system requests prevention of automatic system sleep until the run ends, then releases the request

#### Scenario: Backup run is cancelled or fails
- **WHEN** a real backup run is cancelled or exits with an error
- **THEN** the system releases any active sleep-prevention request before the command finishes

#### Scenario: Dry run
- **WHEN** a user runs `vara backup <profile> --dry-run` on Windows
- **THEN** the system does not request prevention of automatic system sleep

#### Scenario: Display timeout during backup
- **WHEN** the system turns off the display due to its normal display timeout during a real backup
- **THEN** the sleep-prevention request does not require the display to remain on

### Requirement: Sleep-prevention request failure does not block backup
If Windows cannot create or activate the sleep-prevention request, the system SHALL report a warning to standard error and SHALL continue the backup without sleep prevention. Failure to release an active request SHALL also be reported to standard error and SHALL NOT replace or suppress the backup's outcome.

#### Scenario: Windows refuses request activation
- **WHEN** Windows cannot create or activate the sleep-prevention request for a real backup
- **THEN** the system warns on standard error and continues the backup

#### Scenario: Windows refuses request cleanup
- **WHEN** Windows reports an error while the system releases a sleep-prevention request
- **THEN** the system reports the cleanup error to standard error without changing the backup's outcome
