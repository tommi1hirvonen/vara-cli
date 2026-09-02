## MODIFIED Requirements

### Requirement: One-to-one mirror of source state
After a successful backup run, the target mirror directory SHALL contain exactly the set of files and directories present in the profile's sources (respecting excludes and glob patterns), with each entry located at a mirror path derived from that entry's full absolute source path rather than a path relative to its own source's root, so that entries from different sources can never collide at the same mirror location. For a drive-letter source path, the mirror path SHALL be formed by stripping the colon after the drive letter and preserving every other path segment unchanged (for example, `C:\Users\john\Programming\src\main.py` mirrors to `C\Users\john\Programming\src\main.py` under the target).

#### Scenario: File added to source
- **WHEN** a new file exists under a source path that did not exist in the previous run
- **THEN** after the next backup run, the file exists at the corresponding absolute-path-derived location in the mirror

#### Scenario: File removed from source
- **WHEN** a previously backed-up file is deleted from the source
- **THEN** after the next backup run, the file no longer exists in the mirror

#### Scenario: Two sources share a subfolder or file name at the same depth
- **WHEN** two different sources each contain a file or folder with the same name at the same relative depth (for example, both sources have a top-level `notes.txt`, or both contain a `src\` subfolder)
- **THEN** after a backup run, both entries exist in the mirror at distinct, non-colliding locations derived from each entry's own absolute source path
