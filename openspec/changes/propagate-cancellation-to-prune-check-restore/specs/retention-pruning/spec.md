## ADDED Requirements

### Requirement: Graceful cancellation via Ctrl+C
The system SHALL intercept a single Ctrl+C (or equivalent interrupt signal) during a `vara prune`
run and request a graceful stop instead of allowing the operating system to terminate the process
immediately: the run SHALL stop before starting further top-level units of work (a further
snapshot's eligibility evaluation, a further snapshot's removal, or a further blob's garbage
collection), and SHALL then exit deliberately, reporting that the run was cancelled, without
treating this as an error. Work already fully completed at the point of cancellation (snapshots
already removed, blobs already garbage-collected) SHALL remain committed - a cancelled prune run
SHALL NOT be rolled back. WHEN a second Ctrl+C is received while the graceful stop is still in
progress, the system SHALL terminate immediately without waiting for the current unit of work to
finish, falling back to the existing crash-and-interruption-safety guarantees.

#### Scenario: Single Ctrl+C during garbage collection
- **WHEN** a user presses Ctrl+C once while a prune run is removing unreferenced blobs during
  garbage collection
- **THEN** the system finishes removing the blob currently in progress, does not start removing
  any further blob, reports that the run was cancelled, and exits successfully

#### Scenario: Single Ctrl+C during retention evaluation
- **WHEN** a user presses Ctrl+C once while a prune run is still evaluating which snapshots are
  eligible for removal, before any removal or garbage collection has started
- **THEN** the system stops before removing or garbage-collecting anything, reports that the run
  was cancelled, and exits successfully

#### Scenario: Second Ctrl+C forces immediate termination
- **WHEN** a user presses Ctrl+C a second time while the system is still finishing its current
  unit of work after a first Ctrl+C
- **THEN** the system terminates immediately without completing that unit of work or performing
  any further cleanup beyond the existing crash-and-interruption-safety guarantees
