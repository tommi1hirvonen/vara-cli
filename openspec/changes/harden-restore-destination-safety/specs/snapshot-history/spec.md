## MODIFIED Requirements

### Requirement: Restore never writes into the live mirror
The system SHALL refuse to restore any content to a destination path that resolves inside the
profile's live mirror, regardless of whether an overwrite override was given, so that a restore
can never corrupt the very data it exists to recover. For a recursive directory restore, this
guard SHALL apply independently to every path's own computed destination, not only to a single
top-level destination. Resolving whether a destination is inside the mirror SHALL account for
reparse points (symlinks and junctions): a destination path that lexically appears to lie outside
the mirror, but whose real, final location - after following every reparse point along its path -
lies inside the mirror, SHALL still be treated as inside the mirror and refused.

#### Scenario: Destination resolves inside the profile's live mirror
- **WHEN** a user requests a restore to a destination path that resolves to a location inside the
  profile's live mirror
- **THEN** the system reports a clear error explaining that restoring into the live mirror is not
  permitted, does not write any content, and does not prompt for confirmation

#### Scenario: A recursive restore's computed destination resolves inside the live mirror
- **WHEN** a recursive restore's destination directory, or any individual tracked path's own
  computed destination within it, resolves to a location inside the profile's live mirror
- **THEN** the system reports the same clear error, and performs no part of the restore

#### Scenario: Destination reached only through a reparse point into the mirror
- **WHEN** a user requests a restore to a destination path that does not lexically start with the
  profile's live mirror path, but that path passes through a symlink or junction whose real,
  final location lies inside the profile's live mirror
- **THEN** the system reports the same clear error, and performs no part of the restore

## ADDED Requirements

### Requirement: Restore writes are atomic
The system SHALL write a restored file's content to its destination in a way that never leaves the
destination partially written: the destination SHALL either end up with the fully restored content,
or - if the write is interrupted for any reason - be left exactly as it was before the restore was
attempted. An existing destination file SHALL NOT be removed before the restored content has been
fully and successfully staged.

#### Scenario: Restore completes successfully over an existing destination file
- **WHEN** a user restores content to a destination that already contains a file, with overwrite
  authorized
- **THEN** the destination ends up containing exactly the restored content, and at no point during
  the operation is the destination left empty or missing

#### Scenario: Restore is interrupted while writing over an existing destination file
- **WHEN** a restore to a destination that already contains a file is interrupted before the
  restored content has been fully written (for example, the process is terminated, or the write
  fails partway through)
- **THEN** the destination file retains its original content, unchanged
