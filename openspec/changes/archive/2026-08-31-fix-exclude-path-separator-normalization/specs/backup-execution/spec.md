## MODIFIED Requirements

### Requirement: Source exclusion rules honored
The system SHALL exclude files and directories matching a source's configured exclude list or glob patterns from both scanning and the mirror, treating them as if they do not exist in the source. Matching a nested exclude entry against a scanned path SHALL be insensitive to whether the entry or the scanned path uses a forward slash (`/`) or a backslash (`\`) as its path separator, so an exclude entry written with either separator style matches consistently.

#### Scenario: Excluded subfolder
- **WHEN** a source configures an excluded subfolder
- **THEN** files under that subfolder do not appear in the mirror and are not scanned for changes

#### Scenario: Nested exclude written with the non-native path separator
- **WHEN** a source configures a nested excluded path (for example, a subfolder within a subfolder) using a path separator different from the one the host platform normally produces for relative paths
- **THEN** files under that nested path do not appear in the mirror and are not scanned for changes, the same as if the entry had used the platform's native separator
