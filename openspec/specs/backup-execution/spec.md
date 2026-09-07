# backup-execution Specification

## Purpose

Defines the incremental backup pipeline that keeps a target mirror in sync with a profile's sources while retaining space-efficient version history, so that a single run reflects only what changed and past content remains recoverable.

## Requirements

### Requirement: One-to-one mirror of source state
After a successful backup run, the target mirror directory SHALL contain exactly the set of files and directories present in the profile's sources (respecting excludes and glob patterns), with each entry located at a mirror path derived from that entry's full absolute source path rather than a path relative to its own source's root, so that entries from different sources can never collide at the same mirror location. For a drive-letter source path, the mirror path SHALL be formed by stripping the colon after the drive letter and preserving every other path segment unchanged (for example, `C:\Users\john\Programming\src\main.py` mirrors to `C\Users\john\Programming\src\main.py` under the target). This one-to-one correspondence applies to the portion of each source that was successfully scanned during the run; a source, subtree, or file that could not be scanned is exempted for that run per the "Unreadable files do not abort the run" requirement, and its prior mirror entries and manifest state are left unchanged rather than removed.

#### Scenario: File added to source
- **WHEN** a new file exists under a source path that did not exist in the previous run
- **THEN** after the next backup run, the file exists at the corresponding absolute-path-derived location in the mirror

#### Scenario: File removed from source
- **WHEN** a previously backed-up file is deleted from the source
- **THEN** after the next backup run, the file no longer exists in the mirror

#### Scenario: Two sources share a subfolder or file name at the same depth
- **WHEN** two different sources each contain a file or folder with the same name at the same relative depth (for example, both sources have a top-level `notes.txt`, or both contain a `src\` subfolder)
- **THEN** after a backup run, both entries exist in the mirror at distinct, non-colliding locations derived from each entry's own absolute source path

#### Scenario: Drive-letter-root source is scanned from the actual drive root
- **WHEN** a source path is a bare drive root (for example, `C:\`)
- **THEN** the system scans the drive's actual root directory and everything beneath it, rather
  than any directory implied by the process's current working directory, and the resulting mirror
  reflects the drive root's real contents

### Requirement: Source exclusion rules honored
The system SHALL exclude files and directories matching a source's configured exclude list or glob patterns from both scanning and the mirror, treating them as if they do not exist in the source. Matching a nested exclude entry against a scanned path SHALL be insensitive to whether the entry or the scanned path uses a forward slash (`/`) or a backslash (`\`) as its path separator, so an exclude entry written with either separator style matches consistently.

#### Scenario: Excluded subfolder
- **WHEN** a source configures an excluded subfolder
- **THEN** files under that subfolder do not appear in the mirror and are not scanned for changes

#### Scenario: Nested exclude written with the non-native path separator
- **WHEN** a source configures a nested excluded path (for example, a subfolder within a subfolder) using a path separator different from the one the host platform normally produces for relative paths
- **THEN** files under that nested path do not appear in the mirror and are not scanned for changes, the same as if the entry had used the platform's native separator

### Requirement: Incremental change detection
The system SHALL determine which files changed since the last successful snapshot by comparing current source file size and modification time against the recorded manifest, without reading or transferring unchanged file content. The system SHALL match a scanned source path against the manifest's recorded paths case-insensitively, consistent with the case-insensitive, case-preserving semantics of the filesystems it targets, so that a path differing from a recorded path only in character casing is recognized as the same tracked file rather than an unrelated addition. When the manifest's underlying storage holds multiple historical rows for the same logical path recorded under different casings, resolving that path's current state SHALL deterministically select exactly one of them (the most recently recorded), never nondeterministically vary between runs.

#### Scenario: No changes since last run
- **WHEN** a backup run is started and no source file has changed since the last successful snapshot
- **THEN** the run completes without copying any file content and records a new snapshot reflecting no changes

#### Scenario: File's casing changes without content changing
- **WHEN** a source file's name changes only in character casing (for example, `Photo.JPG` renamed to `photo.jpg`) while its size and modification time remain otherwise consistent with the recorded manifest entry
- **THEN** the file is matched to its existing manifest entry rather than classified as a new addition, its content is not re-transferred, and the prior entry is not left as an orphaned, unreferenced record

#### Scenario: Multiple historically recorded casings of the same path
- **WHEN** the manifest's underlying storage holds more than one historical row for what is, case-insensitively, the same logical path (for example, from casing changes recorded before this capability existed)
- **THEN** resolving that path's current state consistently selects the same, most-recently-recorded row every time, regardless of query evaluation order, rather than nondeterministically alternating between runs

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
The system SHALL NOT follow symbolic links, junctions, or other reparse points encountered while
scanning sources; it SHALL record their existence and target path without traversing into or
copying the linked content. A symlink/junction encountered during a scan SHALL be treated as
observed for that run, so it is never misclassified as a deletion of whatever previously existed
at that path. A path previously tracked as a regular file that is replaced by a symlink or
junction SHALL be recorded as a distinct, non-deleted state change, not silently dropped from the
manifest's current view.

#### Scenario: Symlink encountered during scan
- **WHEN** a source directory contains a symbolic link or junction
- **THEN** the backup run completes without traversing the link's target, and records that a link
  existed at that path

#### Scenario: Previously tracked file replaced by a symlink
- **WHEN** a path that was tracked as a regular file in the previous snapshot is now a symbolic
  link or junction at the same path
- **THEN** the backup run does not classify that path as deleted, and instead records that it is
  now a link, distinct from its prior tracked content

#### Scenario: Symlink present across multiple runs
- **WHEN** a source directory contains a symbolic link or junction that remains present, unchanged,
  across two consecutive backup runs
- **THEN** the second run does not classify the link's path as newly added, newly deleted, or
  changed

### Requirement: Unreadable files do not abort the run
WHEN a source file, directory, or other filesystem entry cannot be read (for example,
because it is locked by another process, or the current user lacks permission to
access it), WHEN a configured source's path does not exist as a file or directory at
scan time (for example, an unplugged drive, a drive-letter change, or a
misconfigured path), or WHEN an existing mirror entry cannot be relocated or removed
as part of a move or delete operation (for example, because it is locked by another
process or the current user lacks permission to modify it), the system SHALL record
the failure, skip the affected file, subtree, or source for that run, and continue
backing up the remaining eligible files rather than aborting the entire run or
exiting with an unhandled error. For every file, subtree, or source skipped this way
during scanning, the system SHALL NOT classify any of its previously recorded
manifest paths as deleted for that run; the corresponding mirror entries and
manifest state SHALL remain exactly as they were, to be re-evaluated normally (as
unchanged, changed, or genuinely deleted) once a future run can successfully scan
that location again.

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

#### Scenario: Configured source path does not exist
- **WHEN** a profile's configured source path is neither an existing file nor an
  existing directory at scan time (for example, an unplugged drive, a drive-letter
  change, or a typo'd path)
- **THEN** the backup run completes without an unhandled error, reports the source
  as failed, and does not remove any of that source's previously recorded files
  from the mirror

#### Scenario: Deletion suppressed for files under a permission-denied directory
- **WHEN** a directory's contents cannot be enumerated while scanning, and that
  directory's subtree contains files previously recorded in the manifest
- **THEN** the backup run does not classify those previously recorded files as
  deleted, and their mirror entries and manifest state remain unchanged for that run

#### Scenario: Deletion suppressed for a locked or permission-denied file
- **WHEN** a previously recorded file cannot be read while scanning because it is
  locked by another process or the current user lacks permission to access it
- **THEN** the backup run does not classify that file as deleted, and its mirror
  entry and manifest state remain unchanged for that run

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

### Requirement: Hardlinked mirror entries are read-only
WHEN a mirror entry is placed via a hardlink to its content-store blob, the system SHALL mark that mirror entry read-only, so that an in-place edit through a naive overwrite (for example, opening the file in an editor and saving) fails instead of silently rewriting the shared blob, corrupting that content's historical version record and every other mirror path referencing the same content. WHEN a mirror entry is placed via a real file copy instead of a hardlink, the system SHALL leave that mirror entry writable, since it is an independent physical copy whose in-place edits cannot affect the content-store blob, any other mirror path, or any historical version. WHEN a mirror path's placement kind changes between backup runs (a hardlink placement superseded by a copy-fallback placement, or a copy-fallback placement superseded by a hardlink placement), the system SHALL update the mirror entry's read-only attribute to match the new placement's kind rather than leave it reflecting the prior run's placement kind. The system SHALL establish a hardlink placement's read-only protection from the placement itself, independently of whether the content previously occupying that mirror path is known to the caller, so that protection holds even when a mirror path is re-placed with content it already holds. WHEN a mirror entry is relocated to a mirror path that is already occupied, the system SHALL complete the relocation regardless of either entry's read-only attribute, and the relocated entry SHALL retain the read-only attribute it carried before the relocation.

#### Scenario: File placed via hardlink is read-only
- **WHEN** a backup run places a file's content at a mirror path via a hardlink to its content-store blob
- **THEN** the resulting mirror file is marked read-only, and an attempt to overwrite its content in place is rejected by the filesystem

#### Scenario: File placed via copy fallback remains writable
- **WHEN** a backup run places a file's content at a mirror path via a real copy (because the target filesystem does not support hardlinks, or the blob's hard-link limit was reached)
- **THEN** the resulting mirror file is not marked read-only, and its content can be overwritten in place

#### Scenario: Placement kind changes from hardlink to copy fallback between runs
- **WHEN** a mirror path was previously placed via a hardlink and a later backup run re-places the same mirror path via a real copy (for example, because the blob's hard-link limit was reached)
- **THEN** the mirror entry's read-only attribute is cleared so the newly-copied file is writable

#### Scenario: Placement kind changes from copy fallback to hardlink between runs
- **WHEN** a mirror path was previously placed via a real copy and a later backup run re-places the same mirror path via a hardlink
- **THEN** the mirror entry's read-only attribute is set so the newly-hardlinked file is read-only

#### Scenario: Moving or removing a read-only hardlinked mirror entry still succeeds
- **WHEN** a backup run relocates or removes a mirror entry that was previously marked read-only because it was placed via a hardlink
- **THEN** the move or removal completes successfully, unaffected by the entry's read-only attribute

#### Scenario: Deleting or changing one hardlinked mirror path does not weaken protection on a sibling sharing the same content
- **WHEN** two different mirror paths were both placed via a hardlink to the same content (deduplicated), and a backup run then deletes or changes one of those mirror paths
- **THEN** the other, still-live mirror path remains read-only afterward

#### Scenario: Re-placing a mirror path with the content it already holds keeps it protected
- **WHEN** a mirror path already holds content placed via a hardlink, and a later backup run re-places that same mirror path via a hardlink to that same content without knowing the path's previous content (for example, a run recovering from an interruption that wrote the mirror entry but never recorded it in the manifest, so the file is treated as newly added)
- **THEN** the resulting mirror file is marked read-only, an attempt to overwrite its content in place is rejected by the filesystem, and that content's stored blob is left protected as well

#### Scenario: Relocating a read-only mirror entry onto an already-occupied mirror path succeeds
- **WHEN** a backup run relocates a mirror entry that is read-only because it was placed via a hardlink, and the destination mirror path is already occupied by another read-only mirror entry (for example, a leftover entry written by a previous interrupted run)
- **THEN** the relocation completes successfully rather than being reported as a failed path, the destination holds the relocated entry's content, and the relocated entry remains read-only at its new path

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
