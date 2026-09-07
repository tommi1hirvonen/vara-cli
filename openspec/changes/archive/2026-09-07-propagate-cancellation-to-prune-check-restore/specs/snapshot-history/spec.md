## ADDED Requirements

### Requirement: Graceful cancellation of a directory restore via Ctrl+C
The system SHALL intercept a single Ctrl+C (or equivalent interrupt signal) during a `vara
restore --recursive` run and request a graceful stop instead of allowing the operating system to
terminate the process immediately: the run SHALL stop before writing or removing any further
planned path, and SHALL then exit deliberately, reporting that the restore was cancelled, without
treating this as an error. Paths already written or removed at the point of cancellation SHALL
remain as they are - a cancelled directory restore SHALL NOT roll back paths already applied.
WHEN a second Ctrl+C is received while the graceful stop is still in progress, the system SHALL
terminate immediately without waiting for the path currently being written or removed to finish.

#### Scenario: Single Ctrl+C during a directory restore's writes
- **WHEN** a user presses Ctrl+C once while a recursive restore is writing or removing planned
  paths at the destination
- **THEN** the system finishes the path currently in progress, does not start writing or removing
  any further planned path, reports that the restore was cancelled, and exits successfully

#### Scenario: Cancellation does not roll back already-applied paths
- **WHEN** a recursive restore is cancelled via Ctrl+C after some, but not all, of its planned
  paths have already been written or removed
- **THEN** the paths already written or removed remain at the destination exactly as applied,
  rather than being reverted

#### Scenario: Second Ctrl+C forces immediate termination
- **WHEN** a user presses Ctrl+C a second time while the system is still finishing the path
  currently in progress after a first Ctrl+C
- **THEN** the system terminates immediately without completing that path's write or removal
