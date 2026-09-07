## MODIFIED Requirements

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
