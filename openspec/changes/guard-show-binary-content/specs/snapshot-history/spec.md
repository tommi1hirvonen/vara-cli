## MODIFIED Requirements

### Requirement: Show a version's content directly
The system SHALL provide a command that streams a specific version's content - selected by
version id or by an "as of" date, the same as restore - directly to standard output, without
writing it to any file, so a user can inspect a historical version's content without performing a
restore. Before streaming, WHEN standard output is an interactive terminal (not redirected to a
file or another program) and the resolved version's content is detected as binary from a bounded
initial sample of its bytes, the system SHALL refuse to stream it, unless the user has explicitly
requested to force binary output. WHEN standard output is redirected, the system SHALL stream the
content regardless of whether it is binary, since there is no terminal to protect.

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
