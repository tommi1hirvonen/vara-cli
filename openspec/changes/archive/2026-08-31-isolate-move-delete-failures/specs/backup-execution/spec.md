## MODIFIED Requirements

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
