## ADDED Requirements

### Requirement: Graceful cancellation via Ctrl+C
The system SHALL intercept a single Ctrl+C (or equivalent interrupt signal) during a `vara prune`
run and request a graceful stop instead of allowing the operating system to terminate the process
immediately. Retention evaluation and snapshot removal are one atomic step: WHEN the graceful
stop is requested before that step has started, the system SHALL skip it entirely - removing no
snapshots - and proceed directly to reporting cancellation, rather than partially applying it.
WHEN the graceful stop is requested during garbage collection, the system SHALL stop before
removing any further unreferenced blob. The run SHALL then exit deliberately, reporting that it
was cancelled, without treating this as an error. A step that had already fully completed before
the graceful stop was requested (snapshot removal, or any blob already garbage-collected) SHALL
remain committed - a cancelled prune run SHALL NOT roll back a step that already completed. WHEN
a second Ctrl+C is received while the graceful stop is still in progress, the system SHALL
terminate immediately without waiting for the current unit of work to finish, falling back to the
existing crash-and-interruption-safety guarantees.

#### Scenario: Single Ctrl+C during garbage collection
- **WHEN** a user presses Ctrl+C once while a prune run is removing unreferenced blobs during
  garbage collection
- **THEN** the system finishes removing the blob currently in progress, does not start removing
  any further blob, reports that the run was cancelled, and exits successfully

#### Scenario: Single Ctrl+C before retention evaluation and snapshot removal have started
- **WHEN** a user presses Ctrl+C once before a prune run has started evaluating which snapshots
  are eligible for removal
- **THEN** the system removes no snapshot records, performs no garbage collection, reports that
  the run was cancelled, and exits successfully

#### Scenario: Second Ctrl+C forces immediate termination
- **WHEN** a user presses Ctrl+C a second time while the system is still finishing its current
  unit of work after a first Ctrl+C
- **THEN** the system terminates immediately without completing that unit of work or performing
  any further cleanup beyond the existing crash-and-interruption-safety guarantees
