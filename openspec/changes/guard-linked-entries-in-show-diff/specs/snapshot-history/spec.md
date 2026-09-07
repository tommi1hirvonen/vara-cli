## MODIFIED Requirements

### Requirement: Show a version's content directly
The system SHALL provide a command that streams a specific version's content - selected by
version id or by an "as of" date, the same as restore - directly to standard output, without
writing it to any file, so a user can inspect a historical version's content without performing a
restore. Before streaming, WHEN standard output is an interactive terminal (not redirected to a
file or another program) and the resolved version's content is detected as binary from a bounded
initial sample of its bytes, the system SHALL refuse to stream it, unless the user has explicitly
requested to force binary output. WHEN standard output is redirected, the system SHALL stream the
content regardless of whether it is binary, since there is no terminal to protect. If the resolved
version is a symlink/junction entry rather than one with actual stored content, the system SHALL
report a clear error explaining that this version has no content to show, rather than attempting
to open its content and failing with an unrelated internal error.

#### Scenario: Showing a text file's historical content
- **WHEN** a user requests a specific version of a file's content to be shown
- **THEN** the system writes that version's content to standard output without creating or
  modifying any file on disk

#### Scenario: Showing an unknown path or version
- **WHEN** a user requests a version to be shown for a path never tracked, or a version or date
  that does not exist
- **THEN** the system reports the same clear error already required for restore's equivalent
  case, rather than partial or misleading output

#### Scenario: Refusing binary content to an interactive terminal
- **WHEN** a user requests a version to be shown whose content is detected as binary from a
  bounded initial sample of its bytes, and standard output is an interactive terminal
- **THEN** the system reports a clear error naming the path and explaining that the content
  appears to be binary, without writing any of it to standard output

#### Scenario: Binary content streamed when output is redirected
- **WHEN** a user requests a version to be shown whose content is detected as binary, and
  standard output is redirected to a file or another program rather than an interactive terminal
- **THEN** the system streams the content exactly as it would for text content, without refusing

#### Scenario: Binary content forced to an interactive terminal
- **WHEN** a user requests a version to be shown whose content is detected as binary, standard
  output is an interactive terminal, and the user has explicitly requested to force binary output
- **THEN** the system streams the content instead of refusing

#### Scenario: Showing a linked (symlink/junction) version
- **WHEN** a user requests a version to be shown whose resolved version is a symlink/junction
  entry with no stored content
- **THEN** the system reports a clear error naming the path and explaining that this version has
  no content to show, rather than an unhandled/internal error

### Requirement: Diff two versions of a file
The system SHALL provide a command that shows a textual difference between two versions of the
same path, each selected by version id or by an "as of" date, so a user can see what changed
between two points in a file's history without restoring either version. Before reading either
version's full content, the system SHALL refuse the comparison - naming the offending side(s) and,
for an oversized refusal, its size - when either version's recorded size exceeds 10 MB, or when
either version's content is detected as binary from a bounded initial sample of its bytes, rather
than buffering a large or binary version fully into memory and diffing it as if it were text. If
either resolved version is a symlink/junction entry rather than one with actual stored content,
the system SHALL report a clear error naming the offending side(s) and explaining that they have
no content to diff, rather than attempting to open that side's content and failing with an
unrelated internal error; any content stream already opened for the other, unaffected side SHALL
be released rather than left open.

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

#### Scenario: Diffing a linked (symlink/junction) version
- **WHEN** a user requests a diff and at least one of the two resolved versions is a
  symlink/junction entry with no stored content
- **THEN** the system reports a clear error naming which side(s) have no content to diff, rather
  than an unhandled/internal error

#### Scenario: One side linked, the other side's content already opened
- **WHEN** a user requests a diff where one resolved version's content has already been
  successfully opened for reading and the other resolved version is then found to be a
  symlink/junction entry with no stored content
- **THEN** the system releases the already-opened content before reporting the linked-entry error,
  rather than leaving it open
