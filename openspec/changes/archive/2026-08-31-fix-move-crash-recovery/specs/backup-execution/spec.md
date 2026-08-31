## MODIFIED Requirements

### Requirement: Manifest writes are batched per snapshot
The system SHALL group a snapshot's file-version manifest writes into a bounded number of committed transactions rather than committing each write independently, so that manifest write durability overhead does not dominate a run's execution time when many files change. An interruption before a batch's transaction commits SHALL NOT corrupt the manifest or leave a partially-written row; any mirror content already written for files in that batch remains intact, and the affected paths are re-detected and re-recorded on the next run without error, including when the affected operation was a move whose mirror-side relocation had already completed before the interruption.

#### Scenario: Many small files backed up
- **WHEN** a backup run transfers a large number of small files
- **THEN** the run does not require a separate durable commit for every individual file's manifest write, and total manifest write overhead does not dominate the run's execution time

#### Scenario: Crash mid-batch leaves mirror and manifest consistent
- **WHEN** a backup run is interrupted after a file's content has been written to the mirror but before the manifest transaction covering that file's write has committed
- **THEN** after restart, the interrupted snapshot is recorded as incomplete, no partial or corrupted manifest row exists for the affected path, and the next backup run detects the affected file as needing to be recorded and records it without error

#### Scenario: Crash between a move's mirror relocation and its manifest commit
- **WHEN** a backup run relocates a moved file within the mirror to its new path but is interrupted before the manifest transaction recording that move commits, so the next run's manifest still shows the file at its old path
- **THEN** the next backup run detects the file as moved to the new path, records the move in the manifest so its version history carries forward, and does not report the path as failed even though the mirror-side relocation was already completed by the interrupted run
