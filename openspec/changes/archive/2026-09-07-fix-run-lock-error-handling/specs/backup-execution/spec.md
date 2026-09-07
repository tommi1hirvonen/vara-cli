## MODIFIED Requirements

### Requirement: Concurrent run prevention
The system SHALL prevent two backup runs from executing concurrently against the same profile's target. Because the lock is held per-target rather than per-profile, the system's reported message SHALL describe the target as busy rather than asserting that the requesting profile itself is the one already running, so the message remains accurate when two different profiles share the same target. WHEN the system cannot determine whether a run is already in progress because acquiring the lock failed for a reason other than the lock already being held (for example, a filesystem permission error opening the lock file), the system SHALL report a clear, distinct error identifying the problem rather than allowing an unhandled exception to propagate or reporting that a run is already in progress.

#### Scenario: Second run started while one is in progress
- **WHEN** a user starts a backup run for a profile while another run for the same profile is already in progress
- **THEN** the system refuses to start the second run and reports that a run is already in progress for that target

#### Scenario: Second run started for a different profile sharing the same target
- **WHEN** a user starts a backup run for a profile while another run for a different profile that shares the same target root is already in progress
- **THEN** the system refuses to start the second run and reports that the target is already in use, without asserting that the second profile itself is the one running

#### Scenario: Lock file inaccessible due to filesystem permissions
- **WHEN** a user starts a backup run and the run lock file cannot be opened due to a filesystem permission error, rather than because another run already holds it
- **THEN** the system reports a clear error identifying that the run lock could not be acquired due to a permission problem, rather than reporting that a run is already in progress or allowing an unhandled exception to propagate
