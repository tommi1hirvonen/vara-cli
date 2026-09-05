## MODIFIED Requirements

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
