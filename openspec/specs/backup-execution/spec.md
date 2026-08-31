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
The system SHALL exclude files and directories matching a source's configured exclude list or glob patterns from both scanning and the mirror, treating them as if they do not exist in the source. Matching a nested exclude entry against a scanned path SHALL be insensitive to whether the entry or the scanned path uses a forward slash (`/`) or a backslash (`\`) as its path separator, so an exclude entry written with either separator style matches consistently.

#### Scenario: Excluded subfolder
- **WHEN** a source configures an excluded subfolder
- **THEN** files under that subfolder do not appear in the mirror and are not scanned for changes

#### Scenario: Nested exclude written with the non-native path separator
- **WHEN** a source configures a nested excluded path (for example, a subfolder within a subfolder) using a path separator different from the one the host platform normally produces for relative paths
- **THEN** files under that nested path do not appear in the mirror and are not scanned for changes, the same as if the entry had used the platform's native separator

### Requirement: Incremental change detection
The system SHALL determine which files changed since the last successful snapshot by comparing current source file size and modification time against the recorded manifest, without reading or transferring unchanged file content. The system SHALL match a scanned source path against the manifest's recorded paths case-insensitively, consistent with the case-insensitive, case-preserving semantics of the filesystems it targets, so that a path differing from a recorded path only in character casing is recognized as the same tracked file rather than an unrelated addition.

#### Scenario: No changes since last run
- **WHEN** a backup run is started and no source file has changed since the last successful snapshot
- **THEN** the run completes without copying any file content and records a new snapshot reflecting no changes

#### Scenario: File's casing changes without content changing
- **WHEN** a source file's name changes only in character casing (for example, `Photo.JPG` renamed to `photo.jpg`) while its size and modification time remain otherwise consistent with the recorded manifest entry
- **THEN** the file is matched to its existing manifest entry rather than classified as a new addition, its content is not re-transferred, and the prior entry is not left as an orphaned, unreferenced record

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

### Requirement: Move detection avoids unbounded reads for non-matching candidates
WHEN comparing an added file against same-size deleted candidates during move detection, the system SHALL rule out a candidate using no more than a small bounded read of the added file's content whenever the candidate's previously recorded content signature makes a match impossible, escalating to a full-content read and comparison only when a match remains possible after that bounded read. This SHALL rely only on content the system durably recorded for the deleted candidate at the time it was last backed up; it SHALL NOT require re-reading the deleted candidate's content, which is no longer assumed to exist at its original location. A file's final move-versus-add classification SHALL still be governed by an exact full-content match, unaffected by this optimization.

#### Scenario: Same-size, differing-content candidate ruled out cheaply
- **WHEN** an added file shares its size with one or more deleted candidates, and a small bounded read of the added file's content already differs from every one of those candidates' recorded signatures
- **THEN** move detection rules out those candidates without reading the rest of the added file's content, and the file is treated as a new addition

#### Scenario: Full content match still required to confirm a move
- **WHEN** an added file's bounded-read signature matches a same-size deleted candidate's previously recorded signature
- **THEN** move detection reads the remainder of the added file's content and confirms an exact match against the candidate's full recorded content hash before classifying the file as moved

#### Scenario: Recorded signature unavailable for a candidate
- **WHEN** a deleted candidate has no usable previously recorded content signature (for example, because it was recorded before this capability existed, or under an incompatible recording scheme)
- **THEN** move detection falls back to a full-content read and comparison for that candidate instead of failing or misclassifying the file

### Requirement: Symlinks and junctions are not followed
The system SHALL NOT follow symbolic links, junctions, or other reparse points encountered while scanning sources; it SHALL record their existence and target path without traversing into or copying the linked content.

#### Scenario: Symlink encountered during scan
- **WHEN** a source directory contains a symbolic link or junction
- **THEN** the backup run completes without traversing the link's target, and records that a link existed at that path

### Requirement: Unreadable files do not abort the run
WHEN a source file, directory, or other filesystem entry cannot be read (for example,
because it is locked by another process, or the current user lacks permission to
access it), or WHEN an existing mirror entry cannot be relocated or removed as part
of a move or delete operation (for example, because it is locked by another process
or the current user lacks permission to modify it), the system SHALL record the
failure, skip the affected file or subtree for that run, and continue backing up the
remaining eligible files rather than aborting the entire run or exiting with an
unhandled error.

#### Scenario: Locked file encountered
- **WHEN** a file in scope for backup is locked by another process and cannot be read
- **THEN** the backup run completes, reports the file as failed, and backs up all
  other eligible files

#### Scenario: Permission-denied file encountered during scanning
- **WHEN** a file's metadata cannot be read while scanning because the current user
  lacks permission to access it
- **THEN** the backup run completes without an unhandled error, reports the file as
  failed, and backs up all other eligible files

#### Scenario: Permission-denied directory encountered during scanning
- **WHEN** a directory's contents cannot be enumerated while scanning because the
  current user lacks permission to access it
- **THEN** the backup run completes, the run's result records that the directory's
  subtree was skipped due to the failure, and the system backs up all other
  eligible files outside that subtree

#### Scenario: Locked mirror entry encountered during move
- **WHEN** a file that moved in the source cannot be relocated within the mirror
  because the existing mirror entry is locked by another process
- **THEN** the backup run completes without an unhandled error, reports the affected
  path as failed, and backs up all other eligible files

#### Scenario: Locked mirror entry encountered during delete
- **WHEN** a file removed from the source cannot be removed from the mirror because
  the existing mirror entry is locked by another process
- **THEN** the backup run completes without an unhandled error, reports the affected
  path as failed, and backs up all other eligible files

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

### Requirement: Graceful degradation when a blob's hard-link limit is reached
WHEN the target filesystem supports hardlinks but placing a specific stored blob at a
mirror path fails because that blob has already reached the filesystem's per-file
hard-link limit, the system SHALL fall back to placing a real copy of the content at
that mirror path instead of reporting the file as failed, and SHALL record the file's
version normally as if the placement had succeeded via hardlink.

#### Scenario: Blob already referenced at the hard-link limit
- **WHEN** a backup run needs to place a stored blob at a new mirror path, and that
  blob is already referenced by the maximum number of hard links the target
  filesystem allows for a single file
- **THEN** the backup run places a real copy of the content at that mirror path,
  does not report the file as failed, and records the file's version in the
  manifest

#### Scenario: Failure recovers on a later run without permanent degradation
- **WHEN** a mirror path was previously placed via a real copy because its blob's
  hard-link limit was reached, and a later backup run needs to place the same or a
  different blob at a mirror path referencing that same content
- **THEN** the system attempts a hardlink for that placement rather than assuming a
  copy is always required, falling back to a copy again only if the hardlink attempt
  still fails

### Requirement: Configurable backup-stage concurrency
The system SHALL bound the move-detection scan stage's and the file-transfer stage's concurrency independently, using a profile's configured scan and transfer concurrency limits (per the profile-config capability) when present. When a profile does not configure a stage's concurrency limit, the system SHALL fall back to that stage's own default rather than sharing a single default between stages: the scan stage's default SHALL scale with the number of available processors, while the transfer stage's default SHALL be a fixed low value suited to a target disk that performs best when written to by one stream at a time.

#### Scenario: Default transfer concurrency serializes transfers
- **WHEN** a profile does not configure `transfer_concurrency`
- **THEN** the backup run's transfer stage processes at most one file addition or change at a time

#### Scenario: Default scan concurrency scales with available processors
- **WHEN** a profile does not configure `scan_concurrency`
- **THEN** the backup run's move-detection scan stage may process multiple candidate files concurrently, up to the number of available processors

#### Scenario: Configured transfer concurrency overrides the default
- **WHEN** a profile configures `transfer_concurrency` to a value greater than the default
- **THEN** the backup run's transfer stage may process up to that many file additions or changes concurrently

#### Scenario: Configured scan concurrency overrides the default
- **WHEN** a profile configures `scan_concurrency` to a specific value
- **THEN** the backup run's move-detection scan stage bounds its concurrency to that value
