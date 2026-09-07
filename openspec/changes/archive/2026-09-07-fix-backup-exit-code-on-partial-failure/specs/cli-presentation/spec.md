## ADDED Requirements

### Requirement: Outcome severity determines process exit code
The system SHALL map each of the three outcome severities already defined by the
"Outcome severity is visually distinct" requirement (full success, success with
partial failures, hard error) to a distinct process exit code, consistently across
every command that can produce that severity, so a caller (for example, a
Task Scheduler job or a script) can distinguish the three outcomes without parsing
console output. A full success SHALL exit `0`. A hard error SHALL exit `1`. A success
with partial failures SHALL exit a distinct non-zero code from a hard error, so a
caller does not need to treat "some files were skipped" the same as "the run did not
complete at all".

#### Scenario: Command completes with no failures
- **WHEN** a command completes with no failures of any kind
- **THEN** the process exits with code `0`

#### Scenario: Command completes with partial failures
- **WHEN** a command completes overall but one or more individual items (for example,
  files) failed
- **THEN** the process exits with a non-zero code distinct from the hard-error exit
  code

#### Scenario: Command reports a hard error
- **WHEN** any command reports a hard error that prevented it from completing
- **THEN** the process exits with code `1`, regardless of which command produced the
  error
