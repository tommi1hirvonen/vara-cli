## MODIFIED Requirements

### Requirement: One-to-one mirror of source state
After a successful backup run, the target mirror directory SHALL contain exactly the set of files and directories present in the profile's sources (respecting excludes and glob patterns), with each entry located at a mirror path derived from that entry's full absolute source path rather than a path relative to its own source's root, so that entries from different sources can never collide at the same mirror location. For a drive-letter source path, the mirror path SHALL be formed by stripping the colon after the drive letter and preserving every other path segment unchanged (for example, `C:\Users\john\Programming\src\main.py` mirrors to `C\Users\john\Programming\src\main.py` under the target). A source path for which no mirror path derivation is defined (for example, a UNC network path such as `\\srv\share\file.txt`) SHALL NOT be mirrored by falling back to any other location, including the source path itself or a location outside the target root; every entry from such a source SHALL instead be treated as failed per the "Mirror writes are confined to the target root" requirement. This one-to-one correspondence applies to the portion of each source that was successfully scanned and successfully mirrored during the run; a source, subtree, or file that could not be scanned or could not be mirrored is exempted for that run per the "Unreadable files do not abort the run" requirement, and its prior mirror entries and manifest state are left unchanged rather than removed.

#### Scenario: File added to source
- **WHEN** a new file is added to a configured source
- **THEN** the file appears in the target mirror at its next backup run

#### Scenario: File removed from source
- **WHEN** a previously backed-up file is removed from a configured source
- **THEN** the file is removed from the target mirror at its next backup run

#### Scenario: Two sources share a subfolder or file name at the same depth
- **WHEN** two configured sources each contain an entry that would collide if mapped relative to each source's own root
- **THEN** each entry is mirrored at a distinct path derived from its own full absolute source path, and neither entry overwrites or is skipped in favor of the other

#### Scenario: Drive-letter-root source is scanned from the actual drive root
- **WHEN** a configured source path is a drive root (for example, `D:\`)
- **THEN** every file and directory under that drive is mirrored under the corresponding drive-letter segment of the target root

#### Scenario: UNC source path produces no mirror entries
- **WHEN** a configured source path is a UNC network path (for example, `\\srv\share\file.txt`)
- **THEN** the backup run does not create, modify, or set attributes on any file at that UNC path or at any location outside the target root, and reports every entry from that source as failed per the "Mirror writes are confined to the target root" requirement

## ADDED Requirements

### Requirement: Mirror writes are confined to the target root
Before placing, relocating, or removing a mirror entry, the system SHALL verify that the entry's
resolved mirror path is located inside the target root, and SHALL refuse to perform the write when
it is not. A refused write SHALL be treated the same as any other unreadable-file failure per the
"Unreadable files do not abort the run" requirement: the affected path is recorded as failed and
skipped, the run continues backing up all other eligible files, and the affected path's previously
recorded manifest state (if any) is left unchanged rather than classified as deleted or updated.
This check SHALL apply regardless of why a mirror path would resolve outside the target root,
including but not limited to a source path with no defined mirror-path mapping (for example, a UNC
network path) and a corrupted or malformed relative path recorded in the manifest.

#### Scenario: Placing a file whose mirror path would escape the target root
- **WHEN** the backup run attempts to place a file at a resolved mirror path that is outside the
  target root
- **THEN** the system does not create or modify any file at that resolved path, reports the
  affected path as failed, and continues backing up all other eligible files

#### Scenario: Moving a file whose resolved mirror path would escape the target root
- **WHEN** the backup run attempts to relocate a mirror entry to or from a resolved mirror path
  that is outside the target root
- **THEN** the system does not move, create, or delete any file at that resolved path, reports the
  affected path as failed, and continues backing up all other eligible files

#### Scenario: Removing a file whose resolved mirror path would escape the target root
- **WHEN** the backup run attempts to remove a mirror entry at a resolved mirror path that is
  outside the target root
- **THEN** the system does not delete any file at that resolved path, reports the affected path as
  failed, and continues backing up all other eligible files

#### Scenario: Source's original file is never touched by a refused mirror write
- **WHEN** a refused mirror write's resolved path happens to coincide with the original source
  file (for example, an unmapped UNC source path resolving to itself)
- **THEN** the system does not create, overwrite, hardlink to, or change the attributes (including
  read-only) of that source file
