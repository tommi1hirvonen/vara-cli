## ADDED Requirements

### Requirement: Graceful cancellation via Ctrl+C
The system SHALL intercept a single Ctrl+C (or equivalent interrupt signal) during a `vara check`
run and request a graceful stop instead of allowing the operating system to terminate the process
immediately: the run SHALL stop before verifying any further referenced blob, and SHALL then exit
deliberately, reporting that the run was cancelled, without treating this as an error and without
reporting a non-zero, scriptable "problems found" status on account of the cancellation itself.
Blobs already verified before cancellation SHALL be reported exactly as they would be for a
completed run. WHEN a second Ctrl+C is received while the graceful stop is still in progress, the
system SHALL terminate immediately without waiting for the blob currently being verified to
finish.

#### Scenario: Single Ctrl+C during verification
- **WHEN** a user presses Ctrl+C once while a check run is verifying referenced blobs against the
  manifest
- **THEN** the system finishes verifying the blob currently in progress, does not start verifying
  any further blob, reports that the run was cancelled, and exits successfully

#### Scenario: Cancelled run reports problems found before cancellation
- **WHEN** a user presses Ctrl+C during a check run after at least one missing or corrupt blob has
  already been found
- **THEN** the system still reports the problems found before cancellation, in addition to
  reporting that the run was cancelled

#### Scenario: Second Ctrl+C forces immediate termination
- **WHEN** a user presses Ctrl+C a second time while the system is still finishing verification of
  the blob in progress after a first Ctrl+C
- **THEN** the system terminates immediately without completing that verification
