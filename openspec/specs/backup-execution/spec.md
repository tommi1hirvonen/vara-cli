# backup-execution Specification

## Purpose

Defines the incremental backup pipeline that keeps a target mirror in sync with a profile's sources while retaining space-efficient version history, so that a single run reflects only what changed and past content remains recoverable.

## Requirements

### Requirement: One-to-one mirror of source state
After a successful backup run, the target mirror directory SHALL contain exactly the set of files and directories present in the profile's sources (respecting excludes and glob patterns), with matching relative paths and names.

#### Scenario: File added to source
- **WHEN** a new file exists under a source path that did not exist in the previous run
- **THEN** after the next backup run, the file exists at the corresponding path in the mirror

#### Scenario: File removed from source
- **WHEN** a previously backed-up file is deleted from the source
- **THEN** after the next backup run, the file no longer exists in the mirror

### Requirement: Source exclusion rules honored
The system SHALL exclude files and directories matching a source's configured exclude list or glob patterns from both scanning and the mirror, treating them as if they do not exist in the source.

#### Scenario: Excluded subfolder
- **WHEN** a source configures an excluded subfolder
- **THEN** files under that subfolder do not appear in the mirror and are not scanned for changes

### Requirement: Incremental change detection
The system SHALL determine which files changed since the last successful snapshot by comparing current source file size and modification time against the recorded manifest, without reading or transferring unchanged file content.

#### Scenario: No changes since last run
- **WHEN** a backup run is started and no source file has changed since the last successful snapshot
- **THEN** the run completes without copying any file content and records a new snapshot reflecting no changes

### Requirement: Version history retained on change
WHEN a tracked file's content changes, the system SHALL preserve the file's previous content so it remains retrievable as history, without duplicating already-stored identical content.

#### Scenario: Overwritten file remains recoverable
- **WHEN** a file's content changes and a backup run completes
- **THEN** the file's content prior to the change remains retrievable as a historical version, and its current content is reflected in the mirror

### Requirement: Content deduplication
The system SHALL avoid storing duplicate physical copies of identical file content within a profile, whether the duplication would occur across different files, across historical versions of a file, or between the live mirror and its version history.

#### Scenario: Identical content in two files
- **WHEN** two different files in a profile's sources have byte-identical content
- **THEN** the additional physical storage consumed by the second file's content is negligible compared to the file's size

### Requirement: Graceful degradation on filesystems without hardlink support
WHEN the target filesystem does not support hardlinks, the system SHALL continue to back up successfully using real file copies instead of failing, while still deduplicating identical content within the historical version store.

#### Scenario: exFAT target
- **WHEN** a profile's target is on a filesystem without hardlink support
- **THEN** backup runs still complete successfully, the mirror is still a full one-to-one copy of the source, and previously-stored identical historical content is still not duplicated in the version store

### Requirement: Move and rename detection
WHEN a file's content is unchanged but its path has changed since the last snapshot, the system SHALL relocate the file within the mirror without re-transferring its content.

#### Scenario: File moved to a new folder
- **WHEN** a file is moved from one path to another within a source without modifying its content
- **THEN** after the next backup run, the file appears at the new path in the mirror without its content being re-transferred, and its version history carries forward

### Requirement: Symlinks and junctions are not followed
The system SHALL NOT follow symbolic links, junctions, or other reparse points encountered while scanning sources; it SHALL record their existence and target path without traversing into or copying the linked content.

#### Scenario: Symlink encountered during scan
- **WHEN** a source directory contains a symbolic link or junction
- **THEN** the backup run completes without traversing the link's target, and records that a link existed at that path

### Requirement: Unreadable files do not abort the run
WHEN a source file cannot be read (for example, because it is locked by another process), the system SHALL record the failure, skip the file for that run, and continue backing up the remaining files rather than aborting the entire run.

#### Scenario: Locked file encountered
- **WHEN** a file in scope for backup is locked by another process and cannot be read
- **THEN** the backup run completes, reports the file as failed, and backs up all other eligible files

### Requirement: Crash and interruption safety
The system SHALL apply changes to the mirror and version store such that an interrupted backup run (for example, due to process termination or power loss) leaves the mirror in a valid state consistent with some point during the run, never a partially-written or corrupted file.

#### Scenario: Run interrupted mid-way
- **WHEN** a backup run is terminated before completion
- **THEN** every file present in the mirror afterward contains either its previous complete content or its new complete content, and the interrupted snapshot is recorded as incomplete rather than successful

### Requirement: Concurrent run prevention
The system SHALL prevent two backup runs from executing concurrently against the same profile's target.

#### Scenario: Second run started while one is in progress
- **WHEN** a user starts a backup run for a profile while another run for the same profile is already in progress
- **THEN** the system refuses to start the second run and reports that a run is already in progress
