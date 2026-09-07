## ADDED Requirements

### Requirement: File modified during its own transfer is not recorded under stale metadata
After reading and storing a file's content during the transfer stage, the system SHALL re-check
the source file's size and modification time against the values captured when it was scanned.
WHEN they differ, the system SHALL treat the operation as a failure for the current run - recording
the path as failed and leaving any previously recorded manifest state for that path unchanged -
rather than recording a manifest entry that pairs the just-read content with the stale,
scan-time modification time.

#### Scenario: File unchanged between scan and transfer
- **WHEN** a file's size and modification time at the moment its content is read and stored match
  the values captured when it was scanned
- **THEN** the system records the manifest entry as usual, using the scan-time modification time

#### Scenario: File modified after being scanned but before its content is read
- **WHEN** a file's size or modification time, re-checked immediately after its content is read and
  stored during transfer, differs from the values captured when it was scanned
- **THEN** the system does not write a manifest entry for that read, does not place the read
  content at the file's mirror path, records the path as a failed operation for the run, and
  leaves the path's previously recorded manifest state unchanged

#### Scenario: File repeatedly modified during a large backup run
- **WHEN** a file affected by the previous scenario is scanned again on a subsequent run
- **THEN** its current on-disk size and modification time still differ from its last successfully
  recorded manifest state, so the incremental change detection requirement selects it for
  transfer again
