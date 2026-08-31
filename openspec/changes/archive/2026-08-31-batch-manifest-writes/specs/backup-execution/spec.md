## ADDED Requirements

### Requirement: Manifest writes are batched per snapshot
The system SHALL group a snapshot's file-version manifest writes into a bounded number of committed transactions rather than committing each write independently, so that manifest write durability overhead does not dominate a run's execution time when many files change. An interruption before a batch's transaction commits SHALL NOT corrupt the manifest or leave a partially-written row; any mirror content already written for files in that batch remains intact, and the affected paths are re-detected and re-recorded on the next run.

#### Scenario: Many small files backed up
- **WHEN** a backup run transfers a large number of small files
- **THEN** the run does not require a separate durable commit for every individual file's manifest write, and total manifest write overhead does not dominate the run's execution time

#### Scenario: Crash mid-batch leaves mirror and manifest consistent
- **WHEN** a backup run is interrupted after a file's content has been written to the mirror but before the manifest transaction covering that file's write has committed
- **THEN** after restart, the interrupted snapshot is recorded as incomplete, no partial or corrupted manifest row exists for the affected path, and the next backup run detects the affected file as needing to be recorded and records it without error
