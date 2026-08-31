## ADDED Requirements

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
