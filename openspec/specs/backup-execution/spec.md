# backup-execution Specification

## Purpose

Defines the incremental backup pipeline that keeps a target mirror in sync with a profile's sources while retaining space-efficient version history, so that a single run reflects only what changed and past content remains recoverable.

## Requirements

### Requirement: Backup command accepts profile as a positional argument
The system SHALL provide a `vara backup <profile>` command that requires the profile name as a positional argument. The command SHALL NOT accept a `--profile` option. Supplying `--profile` or omitting the profile positional argument SHALL be rejected as a syntax error.

#### Scenario: Running backup with positional profile argument
- **WHEN** a user runs `vara backup <profile>` with a valid profile name as a positional argument
- **THEN** the system resolves the profile by name and executes the incremental backup pipeline

#### Scenario: Running backup without a positional profile argument
- **WHEN** a user runs `vara backup` without specifying a profile argument
- **THEN** the system rejects the invocation with an error indicating the required profile argument is missing

#### Scenario: Running backup with legacy --profile option
- **WHEN** a user runs `vara backup --profile <name>`
- **THEN** the system rejects the option as unrecognized

### Requirement: One-to-one mirror of source state
After a successful backup run, the target mirror directory SHALL contain exactly the set of files and directories present in the profile's sources (respecting excludes and glob patterns), with each entry located at a mirror path derived from that entry's full absolute source path rather than a path relative to its own source's root, so that entries from different sources can never collide at the same mirror location. For a drive-letter source path, the mirror path SHALL be formed by stripping the colon after the drive letter and preserving every other path segment unchanged (for example, `C:\Users\john\Programming\src\main.py` mirrors to `C\Users\john\Programming\src\main.py` under the target). A source path for which no mirror path derivation is defined (for example, a UNC network path such as `\\srv\share\file.txt`) SHALL NOT be mirrored by falling back to any other location, including the source path itself or a location outside the target root; every entry from such a source SHALL instead be treated as failed per the "Mirror writes are confined to the target root" requirement. This one-to-one correspondence applies to the portion of each source that was successfully scanned and successfully mirrored during the run; a source, subtree, or file that could not be scanned or could not be mirrored is exempted for that run per the "Unreadable files do not abort the run" requirement, and its prior mirror entries and manifest state are left unchanged rather than removed.

#### Scenario: File added to source
- **WHEN** a new file is added to a configured source
- **THEN** the file appears in the target mirror at its next backup run

#### Scenario: File removed from source
- **WHEN** a previously backed-up file is removed from a configured source
- **THEN** the file is removed from the target mirror at its next backup run

#### Scenario: Two sources share a subfolder or file name at the same depth
- **WHEN** two configured sources each contain an entry that would collide if mapped relative to each source's own root
- **THEN** each entry is mirrored at a distinct path derived from its own full absolute source path, and neither entry overwrites or is skipped in favor of the other

#### Scenario: Drive-letter-root source is scanned from the actual drive root
- **WHEN** a configured source path is a drive root (for example, `D:\`)
- **THEN** every file and directory under that drive is mirrored under the corresponding drive-letter segment of the target root

#### Scenario: UNC source path produces no mirror entries
- **WHEN** a configured source path is a UNC network path (for example, `\\srv\share\file.txt`)
- **THEN** the backup run does not create, modify, or set attributes on any file at that UNC path or at any location outside the target root, and reports every entry from that source as failed per the "Mirror writes are confined to the target root" requirement

### Requirement: Mirror writes are confined to the target root
Before placing, relocating, or removing a mirror entry, the system SHALL verify that the entry's
resolved mirror path is located inside the target root, and SHALL refuse to perform the write when
it is not. A refused write SHALL be treated the same as any other unreadable-file failure per the
"Unreadable files do not abort the run" requirement: the affected path is recorded as failed and
skipped, the run continues backing up all other eligible files, and the affected path's previously
recorded manifest state (if any) is left unchanged rather than classified as deleted or updated.
This check SHALL apply regardless of why a mirror path would resolve outside the target root,
including but not limited to a source path with no defined mirror-path mapping (for example, a UNC
network path) and a corrupted or malformed relative path recorded in the manifest.

#### Scenario: Placing a file whose mirror path would escape the target root
- **WHEN** the backup run attempts to place a file at a resolved mirror path that is outside the
  target root
- **THEN** the system does not create or modify any file at that resolved path, reports the
  affected path as failed, and continues backing up all other eligible files

#### Scenario: Moving a file whose resolved mirror path would escape the target root
- **WHEN** the backup run attempts to relocate a mirror entry to or from a resolved mirror path
  that is outside the target root
- **THEN** the system does not move, create, or delete any file at that resolved path, reports the
  affected path as failed, and continues backing up all other eligible files

#### Scenario: Removing a file whose resolved mirror path would escape the target root
- **WHEN** the backup run attempts to remove a mirror entry at a resolved mirror path that is
  outside the target root
- **THEN** the system does not delete any file at that resolved path, reports the affected path as
  failed, and continues backing up all other eligible files

#### Scenario: Source's original file is never touched by a refused mirror write
- **WHEN** a refused mirror write's resolved path happens to coincide with the original source
  file (for example, an unmapped UNC source path resolving to itself)
- **THEN** the system does not create, overwrite, hardlink to, or change the attributes (including
  read-only) of that source file

### Requirement: Source exclusion rules honored
The system SHALL exclude files and directories matching a source's configured exclude list or glob patterns from both scanning and the mirror, treating them as if they do not exist in the source. Matching a nested exclude entry against a scanned path SHALL be insensitive to whether the entry or the scanned path uses a forward slash (`/`) or a backslash (`\`) as its path separator, so an exclude entry written with either separator style matches consistently. A directory matching the source's literal exclude list SHALL NOT be traversed at all: the system SHALL NOT enumerate its contents, and any file or subdirectory beneath it - whether readable or not - SHALL NOT be scanned, reported as a scan failure, or considered for the mirror.

#### Scenario: Excluded subfolder
- **WHEN** a source configures an excluded subfolder
- **THEN** files under that subfolder do not appear in the mirror and are not scanned for changes

#### Scenario: Nested exclude written with the non-native path separator
- **WHEN** a source configures a nested excluded path (for example, a subfolder within a subfolder) using a path separator different from the one the host platform normally produces for relative paths
- **THEN** files under that nested path do not appear in the mirror and are not scanned for changes, the same as if the entry had used the platform's native separator

#### Scenario: Excluded directory is never traversed
- **WHEN** a source configures a literally excluded directory
- **THEN** the system does not enumerate that directory's contents during the scan, so no I/O is spent walking it

#### Scenario: Excluded directory contains unreadable content
- **WHEN** a source configures a literally excluded directory, and that directory (or something beneath it) would be unreadable due to filesystem permissions
- **THEN** the system does not record a scan failure for that directory or anything beneath it, since it is never traversed

### Requirement: Incremental change detection
The system SHALL determine which files changed since the last successful snapshot by comparing current source file size and modification time against the recorded manifest, without reading or transferring unchanged file content. The system SHALL match a scanned source path against the manifest's recorded paths case-insensitively, consistent with the case-insensitive, case-preserving semantics of the filesystems it targets, so that a path differing from a recorded path only in character casing is recognized as the same tracked file rather than an unrelated addition. This case-insensitivity SHALL apply to the letters of every script, including accented and other non-ASCII letters, and not only to the unaccented Latin letters A to Z. When the manifest's underlying storage holds multiple historical rows for the same logical path recorded under different casings, resolving that path's current state SHALL deterministically select exactly one of them (the most recently recorded), never nondeterministically vary between runs. A deletion recorded for a path under any casing SHALL apply to the logical path, so that no older row recorded under another casing keeps it tracked.

#### Scenario: No changes since last run
- **WHEN** a backup run is started and no source file has changed since the last successful snapshot
- **THEN** the run completes without copying any file content and records a new snapshot reflecting no changes

#### Scenario: File's casing changes without content changing
- **WHEN** a source file's name changes only in character casing (for example, `Photo.JPG` renamed to `photo.jpg`) while its size and modification time remain otherwise consistent with the recorded manifest entry
- **THEN** the file is matched to its existing manifest entry rather than classified as a new addition, its content is not re-transferred, and the prior entry is not left as an orphaned, unreferenced record

#### Scenario: Multiple historically recorded casings of the same path
- **WHEN** the manifest's underlying storage holds more than one historical row for what is, case-insensitively, the same logical path (for example, from casing changes recorded before this capability existed)
- **THEN** resolving that path's current state consistently selects the same, most-recently-recorded row every time, regardless of query evaluation order, rather than nondeterministically alternating between runs

#### Scenario: File with a non-ASCII name changes casing and content
- **WHEN** a source file whose name contains an accented letter is renamed only in the casing of that letter (for example `äiti.txt` renamed to `Äiti.txt`, or the reverse) and is then modified, and two backup runs follow
- **THEN** the first run records one change for the file, the second run records no change and copies no content, and the manifest's current state holds exactly one entry for the file, spelled as the latest row spells it

#### Scenario: Several recorded casings of a non-ASCII path
- **WHEN** the manifest holds rows for the same logical path under two casings that differ only in a non-ASCII letter, such as `Äiti.txt` and `äiti.txt`
- **THEN** the current state holds exactly one entry for it, taken from the most recently recorded row whichever casing that row used, and the answer is the same on every run

#### Scenario: Deletion recorded under another casing of a non-ASCII path
- **WHEN** the manifest holds a live row for `Äiti.txt`, a later live row for `äiti.txt`, and a still later deletion recorded for `äiti.txt`
- **THEN** the path is not part of the current state, and the next backup run does not record it as deleted again

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
copying the linked content. The recorded target path SHALL be stored separately from any
content-store hash, so a link's manifest entry never carries a value that downstream consumers of
content hashes (integrity checking, restore, content-store maintenance) could mistake for one. A
symlink/junction encountered during a scan SHALL be treated as observed for that run, so it is
never misclassified as a deletion of whatever previously existed at that path. A path previously
tracked as a regular file that is replaced by a symlink or junction SHALL be recorded as a
distinct, non-deleted state change, not silently dropped from the manifest's current view, and the
file's now-superseded mirror copy SHALL be removed as part of recording that change, so the mirror
does not retain stale content for a path the manifest no longer considers a regular file.

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

#### Scenario: Mirror copy removed when a tracked file becomes a symlink
- **WHEN** a path tracked as a regular file with a mirrored copy of its content is replaced by a
  symbolic link or junction, and the backup run records that transition
- **THEN** the file's previously mirrored copy is removed from the target as part of the same run,
  so the mirror no longer contains a copy of content the manifest now considers superseded by a
  link

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

### Requirement: Crash and interruption safety
The system SHALL apply changes to the mirror and version store such that an interrupted backup run (for example, due to process termination or power loss) leaves the mirror in a valid state consistent with some point during the run, never a partially-written or corrupted file.

#### Scenario: Run interrupted mid-way
- **WHEN** a backup run is terminated before completion
- **THEN** every file present in the mirror afterward contains either its previous complete content or its new complete content, and the interrupted snapshot is recorded as incomplete rather than successful

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

### Requirement: Dry-run mode reports planned changes without executing them
The system SHALL provide a `--dry-run` option to `vara backup` that performs the same scan, diff, and move-detection planning stages as a real run - so its reported counts reflect actual exclude/glob filtering and move detection - but performs no writes: no content is written to the mirror or content store, no manifest rows are recorded, and no new snapshot record is created. A dry run SHALL still acquire the profile's run lock for the duration of planning, refusing to start under the same conditions a real run would, so its output reflects a consistent view of the manifest and it cannot race a concurrently starting real run. The system SHALL report the planned added/changed/moved/deleted counts, the total bytes that would be transferred, and any paths that could not be scanned (and would therefore be skipped by a real run), instead of a completed-run summary.

#### Scenario: Dry run reports planned changes without writing anything
- **WHEN** a user runs `vara backup --dry-run` for a profile with pending changes
- **THEN** the system reports the counts of files that would be added, changed, moved, and deleted, and the total bytes that would be transferred, and afterward the mirror, content store, and manifest are unchanged - no new snapshot record exists

#### Scenario: Dry run refuses to start while a real run is in progress for the same target
- **WHEN** a user runs `vara backup --dry-run` for a profile while a real backup or prune run against the same target is already in progress
- **THEN** the system refuses to start the dry run, reporting the same shared-target-busy error a real run would report in the same situation

#### Scenario: Dry run reports unreadable paths without treating them as an error
- **WHEN** a user runs `vara backup --dry-run` for a profile where at least one source path cannot be scanned (for example, an unreadable subtree)
- **THEN** the system reports those paths as ones a real run would skip, without failing the dry run itself or requiring `--dry-run` to abort

#### Scenario: No changes pending
- **WHEN** a user runs `vara backup --dry-run` for a profile with no changes since the last run
- **THEN** the system reports zero planned changes and exits successfully

### Requirement: Automatic system sleep is prevented during a real backup
On Windows, the system SHALL request prevention of automatic system sleep for the duration of a real backup run. It SHALL release that request when the run completes, is cancelled, or fails. The request SHALL NOT require the display to remain on.

#### Scenario: Successful real backup
- **WHEN** a real backup run starts on Windows
- **THEN** the system requests prevention of automatic system sleep until the run ends, then releases the request

#### Scenario: Backup run is cancelled or fails
- **WHEN** a real backup run is cancelled or exits with an error
- **THEN** the system releases any active sleep-prevention request before the command finishes

#### Scenario: Dry run
- **WHEN** a user runs `vara backup <profile> --dry-run` on Windows
- **THEN** the system does not request prevention of automatic system sleep

#### Scenario: Display timeout during backup
- **WHEN** the system turns off the display due to its normal display timeout during a real backup
- **THEN** the sleep-prevention request does not require the display to remain on

### Requirement: Sleep-prevention request failure does not block backup
If Windows cannot create or activate the sleep-prevention request, the system SHALL report a warning to standard error and SHALL continue the backup without sleep prevention. Failure to release an active request SHALL also be reported to standard error and SHALL NOT replace or suppress the backup's outcome.

#### Scenario: Windows refuses request activation
- **WHEN** Windows cannot create or activate the sleep-prevention request for a real backup
- **THEN** the system warns on standard error and continues the backup

#### Scenario: Windows refuses request cleanup
- **WHEN** Windows reports an error while the system releases a sleep-prevention request
- **THEN** the system reports the cleanup error to standard error without changing the backup's outcome

### Requirement: Stored content is complete and durable under its content name
The system SHALL NOT make content available in the version store under its content-derived name until all of that content has been written, and SHALL ensure the content has reached persistent storage, not only the operating system's cache, before it takes that name. As a result, an abrupt loss of power during or after a backup run SHALL NOT leave a blob under a content name whose stored bytes are fewer than the content it is named for because the write had not yet reached the disk.

#### Scenario: Store interrupted while the content is being written
- **WHEN** reading a source file or writing its content to the version store fails partway through
- **THEN** no blob exists in the version store under that content's name, and the file is reported as failed

#### Scenario: Content reaches persistent storage before it takes its name
- **WHEN** a backup run stores a file's content in the version store
- **THEN** the content has been flushed to persistent storage before it appears in the version store under its content-derived name

### Requirement: A damaged stored blob is repaired when its content is stored again
WHEN a backup run stores content whose content-derived name is already present in the version store, and the size of the present blob differs from the size of the content just read, the system SHALL replace the present blob's content with the complete content and SHALL NOT report the file as failed. The replacement SHALL be visible through every name that refers to the blob, including mirror entries that are hard links to it, and through every later restore. A present blob whose size equals the size of the content SHALL be left untouched. WHEN several files with the same content are stored in one run, the repair SHALL happen once and no file SHALL fail or be written with incomplete content because of it.

#### Scenario: Present blob is shorter than the content it is named for
- **WHEN** a backup run stores content whose name is present in the version store as a blob shorter than the content
- **THEN** the blob holds the complete content afterwards, the file is recorded in the manifest as it would be for any stored file, and no failure is reported

#### Scenario: Mirror entries linked to a damaged blob see the repaired content
- **WHEN** a mirror entry is a hard link to a blob that is shorter than the content it is named for, and a backup run stores that content again
- **THEN** that mirror entry holds the complete content after the run

#### Scenario: Present blob of the expected size is left untouched
- **WHEN** a backup run stores content whose name is present in the version store as a blob of the same size as the content
- **THEN** the present blob is not rewritten and the temporary copy of the content is discarded

#### Scenario: Several files with the same damaged content are stored in one run
- **WHEN** a backup run stores several files with identical content at the same time, and the version store holds a blob for that content that is shorter than the content
- **THEN** the blob holds the complete content afterwards, every one of the files is recorded without failure, and every mirror entry for them holds the complete content

### Requirement: Change tree options on the backup command
The system SHALL accept the options `--depth <n>`, `--files` and `--interactive` on `vara backup <profile>`. Giving any of them SHALL select the change tree view, which is shown after the command's usual summary, and `--interactive` SHALL imply the tree. `--depth` SHALL accept a non-negative integer, and a negative or non-integer value SHALL be rejected as invalid input before any scan or run starts. The tree SHALL be the directory tree used by `vara snapshot`: directories rooted at the mirror root, each with the added, changed, moved, deleted and linked counts of its whole subtree in the change-kind colors, configured source roots marked as sources, with `--files` adding the changed file paths and their change kinds (and a moved entry's previous path). `--depth <n>` SHALL limit the displayed levels below the root as it does for `vara snapshot`, without changing any count. Without any of the three options, the command's output SHALL be unchanged.

`--json` SHALL NOT be combined with `--depth`, `--files` or `--interactive`: the combination SHALL be rejected as a usage error before any scan or run starts.

#### Scenario: Static tree after a dry run
- **WHEN** a user runs `vara backup <profile> --dry-run --depth 1`
- **THEN** the dry-run summary is shown, followed by the directory tree limited to the root and its immediate affected subdirectories, with counts that include all planned changes beneath each directory

#### Scenario: File paths in the tree
- **WHEN** a user runs `vara backup <profile> --dry-run --files`
- **THEN** the tree also lists each planned file under its directory with its change kind, and a planned move shows the path it moves from

#### Scenario: Unchanged output without tree options
- **WHEN** a user runs `vara backup <profile> --dry-run` or `vara backup <profile>` without `--depth`, `--files` or `--interactive`
- **THEN** the output is the same as before this requirement existed, and no tree is shown

#### Scenario: Rejecting an invalid depth
- **WHEN** a user supplies a negative or non-integer value to `--depth` on `vara backup`
- **THEN** the command rejects the value as invalid input and does not scan or run

#### Scenario: Rejecting a tree option with JSON
- **WHEN** a user runs `vara backup <profile> --json` together with `--depth`, `--files` or `--interactive`
- **THEN** the command reports a usage error and does not scan or run

### Requirement: Dry-run change tree shows the planned changes
When a tree option is given with `--dry-run`, the system SHALL build the tree from the planned changes of the same planning pass that produced the dry-run counts, so the totals of the tree equal the reported added, changed, moved and deleted counts, with planned links shown in the linked count. The system SHALL show the tree after planning has finished and after the run lock has been released. The dry run SHALL continue to perform no writes: no content, no manifest rows, no snapshot record and no manifest file created, whether the tree is static or interactive. A dry run with no planned changes SHALL report zero planned changes and show no tree.

#### Scenario: Tree totals match the dry-run counts
- **WHEN** a user runs `vara backup <profile> --dry-run --depth 0`
- **THEN** the root row's added, changed, moved and deleted counts equal the counts in the dry-run summary

#### Scenario: Dry-run tree writes nothing
- **WHEN** a user runs `vara backup <profile> --dry-run --interactive` and browses the tree
- **THEN** afterward the mirror, content store and manifest are unchanged and no new snapshot record exists

#### Scenario: No planned changes
- **WHEN** a user runs `vara backup <profile> --dry-run --files` for a profile with no changes since the last run
- **THEN** the system reports zero planned changes, shows no tree, and exits successfully

#### Scenario: Run lock is not held while browsing
- **WHEN** a user is browsing the interactive dry-run tree
- **THEN** the profile's run lock has already been released and another run for the same target can start

### Requirement: Real-run change tree shows the recorded snapshot
When a tree option is given on a real run that started and recorded a snapshot, the system SHALL show the tree after the run's summary, built from the changes recorded for that snapshot, exactly as `vara snapshot <id>` would show them, including a run that was cancelled or that had failed paths. A run that was refused before starting, so that no snapshot exists, SHALL show no tree. Reading the snapshot for the tree SHALL NOT modify profile backing storage. The tree SHALL NOT delay or alter the run itself, which completes and records its summary before the tree is read.

#### Scenario: Static tree after a real run
- **WHEN** a user runs `vara backup <profile> --depth 2` and the run records a snapshot
- **THEN** the run summary is shown, followed by the tree of that snapshot's changes limited to two levels, with the same counts `vara snapshot <id> --depth 2` would show

#### Scenario: Tree after a cancelled run
- **WHEN** a user runs `vara backup <profile> --files` and cancels the run with Ctrl+C after some changes were recorded
- **THEN** the run's cancellation summary is shown, followed by the tree of the changes recorded so far

#### Scenario: Refused run shows no tree
- **WHEN** a user runs `vara backup <profile> --interactive` and the run is refused because another run holds the target
- **THEN** the refusal is reported as usual and no tree is shown

#### Scenario: Run with no recorded changes
- **WHEN** a real run with a tree option records a snapshot that changed nothing
- **THEN** the system reports that no changes were recorded for the snapshot instead of an empty tree

### Requirement: Interactive backup tree and its fallback
`--interactive` on `vara backup` SHALL present the tree for keyboard navigation under the same keys, initial expansion, scrolling and terminal restoration as the interactive snapshot change tree, for both dry runs and real runs. The interactive view SHALL start only after the progress display has ended. When standard input or output is redirected, the terminal does not support live rendering, or color is disabled, the system SHALL show the static tree instead, honoring `--depth` and `--files`, and SHALL write a single line to standard error stating that the interactive view is unavailable.

Browsing SHALL NOT change the command's outcome: the exit code SHALL be the one the dry run or real run determined, whether the user quits the view, presses Ctrl+C in it, or the view falls back to the static tree.

#### Scenario: Interactive tree after a dry run
- **WHEN** a user runs `vara backup <profile> --dry-run --interactive` in a terminal and there are planned changes
- **THEN** the dry-run summary is shown, the interactive tree opens, and the user can expand, collapse and scroll it with the snapshot tree's keys

#### Scenario: Interactive tree after a real run
- **WHEN** a user runs `vara backup <profile> --interactive` in a terminal and the run records a snapshot
- **THEN** the run summary is shown, the interactive tree of the recorded changes opens, and quitting returns to the shell

#### Scenario: Fallback when output is redirected
- **WHEN** a user runs `vara backup <profile> --dry-run --interactive` with standard output redirected
- **THEN** the summary and the static tree are written to the output, one line on standard error states that the interactive view is unavailable, and the exit code matches the dry run's outcome

#### Scenario: Cancelled run shows the tree statically
- **WHEN** a user runs `vara backup <profile> --interactive`, cancels the run with Ctrl+C, and the run recorded a snapshot
- **THEN** the cancellation summary is shown, followed by the static tree of the recorded changes, the interactive view is not opened, and no fallback note is written

#### Scenario: Exit code unaffected by quitting
- **WHEN** a real run completes with the success outcome and the user then quits the interactive tree with `q`
- **THEN** the command exits with the success exit code

#### Scenario: Exit code unaffected by Ctrl+C in the tree
- **WHEN** a real run completes with the success outcome and the user then presses Ctrl+C in the interactive tree
- **THEN** the terminal is restored and the command exits with the success exit code

#### Scenario: Partial failure outcome preserved
- **WHEN** a real run ends with a partial-failure outcome and the user quits the interactive tree
- **THEN** the command exits with the partial-failure exit code
