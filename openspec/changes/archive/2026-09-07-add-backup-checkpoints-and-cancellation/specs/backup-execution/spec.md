## MODIFIED Requirements

### Requirement: Manifest writes are checkpointed periodically during a run
The system SHALL commit a snapshot's file-version manifest writes in periodic checkpoints bounded by elapsed time, rather than as either one commit per write or one commit for the entire run, so that the amount of already-recorded work at risk from an interruption is bounded by the checkpoint interval and does not grow with the run's total duration. Each checkpoint commit MUST include every manifest row recorded since the previous checkpoint (or since the run began, for the first checkpoint) together with the snapshot's current progress, and MUST leave the manifest in a valid, uncorrupted state whether or not a later checkpoint in the same run ever commits. An interruption between checkpoints SHALL NOT corrupt the manifest or leave a partially-written row; any mirror content already written for files recorded in a checkpoint that never committed remains intact, and the affected paths are re-detected and re-recorded on a later run without error, including when the affected operation was a move whose mirror-side relocation had already completed before the interruption.

#### Scenario: Many small files backed up
- **WHEN** a backup run transfers a large number of small files
- **THEN** the run does not require a separate durable commit for every individual file's manifest write, and total manifest write overhead does not dominate the run's execution time

#### Scenario: Long-running run interrupted near the end only loses recent progress
- **WHEN** a backup run spanning many checkpoint intervals is interrupted shortly before it would have finished
- **THEN** only the manifest writes recorded since the most recently completed checkpoint are missing on restart, and a later run only needs to re-detect and re-record files from that unfinished interval, not the files already committed in earlier checkpoints of the same run

#### Scenario: Crash mid-checkpoint leaves mirror and manifest consistent
- **WHEN** a backup run is interrupted after a file's content has been written to the mirror but before the checkpoint transaction covering that file's write has committed
- **THEN** after restart, the interrupted snapshot is recorded as incomplete, no partial or corrupted manifest row exists for the affected path, and a later backup run detects the affected file as needing to be recorded and records it without error

#### Scenario: Crash between a move's mirror relocation and its checkpoint commit
- **WHEN** a backup run relocates a moved file within the mirror to its new path but is interrupted before the checkpoint transaction recording that move commits, so a later run's manifest still shows the file at its old path
- **THEN** a later backup run detects the file as moved to the new path, records the move in the manifest so its version history carries forward, and does not report the path as failed even though the mirror-side relocation was already completed by the interrupted run

## ADDED Requirements

### Requirement: Graceful cancellation via Ctrl+C
The system SHALL intercept a single Ctrl+C (or equivalent interrupt signal) during a backup run and request a graceful stop instead of allowing the operating system to terminate the process immediately: in-flight per-file operations SHALL be allowed to finish, no new file operation SHALL be started, an out-of-cycle manifest checkpoint SHALL be committed immediately covering whatever work has completed so far, and the run SHALL then exit deliberately. The interrupted snapshot SHALL be recorded with a `Cancelled` status, distinct from `Failed`, whenever the graceful stop completes its forced checkpoint. WHEN a second Ctrl+C is received while the graceful stop is still in progress, the system SHALL terminate immediately without waiting for in-flight operations or forcing a checkpoint, falling back to the existing crash-and-interruption-safety guarantees (the run is later reconciled as `Failed`, not `Cancelled`, since no orderly checkpoint completed for that final interval).

#### Scenario: Single Ctrl+C during an active transfer
- **WHEN** a user presses Ctrl+C once while a backup run is transferring files
- **THEN** the system stops starting new file operations, waits for in-flight operations to finish, commits a checkpoint covering all work completed so far, records the snapshot as `Cancelled`, and exits without requiring a second interrupt

#### Scenario: Cancelled run resumes like any interrupted run
- **WHEN** a user starts a new backup run for a profile whose previous run was recorded as `Cancelled`
- **THEN** the run's incremental change detection treats files recorded before the cancellation as already backed up, and only re-detects and re-records files that were not yet committed at the time of cancellation

#### Scenario: Second Ctrl+C forces immediate termination
- **WHEN** a user presses Ctrl+C a second time while the system is still finishing in-flight operations after a first Ctrl+C
- **THEN** the process terminates immediately without waiting further, and the run is later reconciled as `Failed` rather than `Cancelled`, consistent with any other unplanned interruption

#### Scenario: Ctrl+C pressed before any file has been transferred
- **WHEN** a user presses Ctrl+C while a backup run is still scanning sources or diffing against the manifest, before any file operation has started
- **THEN** the system exits without transferring any files, and the run is recorded as `Cancelled` even though no manifest checkpoint beyond the run's start was necessary
