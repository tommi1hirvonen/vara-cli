## MODIFIED Requirements

### Requirement: Diff two versions of a file
The system SHALL provide a command that shows a textual difference between two versions of the
same path, each selected by version id or by an "as of" date, so a user can see what changed
between two points in a file's history without restoring either version. Before reading either
version's full content, the system SHALL refuse the comparison - naming the offending side(s) and,
for an oversized refusal, its size - when either version's recorded size exceeds 10 MB, or when
either version's content is detected as binary from a bounded initial sample of its bytes, rather
than buffering a large or binary version fully into memory and diffing it as if it were text.

#### Scenario: Diffing two recorded versions
- **WHEN** a user requests a diff between two versions of the same path, each identified by a
  version id or an "as of" date
- **THEN** the system displays a textual difference between the two versions' content

#### Scenario: Diffing an unknown path or version
- **WHEN** a user requests a diff involving a path never tracked, or a version or date that does
  not exist
- **THEN** the system reports the same clear error already required for restore's equivalent
  case, rather than partial or misleading output

#### Scenario: Refusing an oversized version
- **WHEN** a user requests a diff and at least one of the two resolved versions has a recorded
  size exceeding the diff size limit
- **THEN** the system reports a clear error naming which side(s) exceed the limit and their
  size, without reading either version's full content into memory

#### Scenario: Refusing binary content
- **WHEN** a user requests a diff and at least one of the two resolved versions is detected as
  binary content from a bounded initial sample of its bytes
- **THEN** the system reports a clear error naming which side(s) are binary, without producing a
  byte-level "diff" of that content

#### Scenario: Both versions exceed the size limit
- **WHEN** a user requests a diff and both resolved versions have a recorded size exceeding the
  diff size limit
- **THEN** the system reports a single clear error naming both sides and their sizes, rather than
  only the first side checked

#### Scenario: One version too large, the other binary
- **WHEN** a user requests a diff where one resolved version exceeds the diff size limit and the
  other resolved version (within the size limit) is detected as binary content
- **THEN** the system reports a clear error identifying each side's specific problem, rather than
  a generic or misleading message
