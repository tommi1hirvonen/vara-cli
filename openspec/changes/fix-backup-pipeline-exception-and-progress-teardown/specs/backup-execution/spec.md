## ADDED Requirements

### Requirement: Original failure cause is preserved when failure recording itself fails
WHEN a backup run fails and the system attempts to record that failure (its status and whatever
manifest rows were already produced) before propagating the error, a subsequent failure during
that recording attempt SHALL NOT replace or obscure the original triggering exception. The
original failure SHALL still be the one reported to the caller, regardless of whether recording
the failure itself succeeds.

#### Scenario: Recording a run's failure state itself fails
- **WHEN** a backup run fails partway through, and the system's attempt to durably record that
  failure (before re-reporting the original error) itself throws
- **THEN** the error ultimately reported to the caller is the original failure that caused the run
  to fail, not the secondary error encountered while recording it
