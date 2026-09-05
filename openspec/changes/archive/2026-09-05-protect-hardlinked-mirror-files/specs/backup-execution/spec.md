## ADDED Requirements

### Requirement: Hardlinked mirror entries are read-only
WHEN a mirror entry is placed via a hardlink to its content-store blob, the system SHALL mark that mirror entry read-only, so that an in-place edit through a naive overwrite (for example, opening the file in an editor and saving) fails instead of silently rewriting the shared blob, corrupting that content's historical version record and every other mirror path referencing the same content. WHEN a mirror entry is placed via a real file copy instead of a hardlink, the system SHALL leave that mirror entry writable, since it is an independent physical copy whose in-place edits cannot affect the content-store blob, any other mirror path, or any historical version. WHEN a mirror path's placement kind changes between backup runs (a hardlink placement superseded by a copy-fallback placement, or a copy-fallback placement superseded by a hardlink placement), the system SHALL update the mirror entry's read-only attribute to match the new placement's kind rather than leave it reflecting the prior run's placement kind.

#### Scenario: File placed via hardlink is read-only
- **WHEN** a backup run places a file's content at a mirror path via a hardlink to its content-store blob
- **THEN** the resulting mirror file is marked read-only, and an attempt to overwrite its content in place is rejected by the filesystem

#### Scenario: File placed via copy fallback remains writable
- **WHEN** a backup run places a file's content at a mirror path via a real copy (because the target filesystem does not support hardlinks, or the blob's hard-link limit was reached)
- **THEN** the resulting mirror file is not marked read-only, and its content can be overwritten in place

#### Scenario: Placement kind changes from hardlink to copy fallback between runs
- **WHEN** a mirror path was previously placed via a hardlink and a later backup run re-places the same mirror path via a real copy (for example, because the blob's hard-link limit was reached)
- **THEN** the mirror entry's read-only attribute is cleared so the newly-copied file is writable

#### Scenario: Placement kind changes from copy fallback to hardlink between runs
- **WHEN** a mirror path was previously placed via a real copy and a later backup run re-places the same mirror path via a hardlink
- **THEN** the mirror entry's read-only attribute is set so the newly-hardlinked file is read-only

#### Scenario: Moving or removing a read-only hardlinked mirror entry still succeeds
- **WHEN** a backup run relocates or removes a mirror entry that was previously marked read-only because it was placed via a hardlink
- **THEN** the move or removal completes successfully, unaffected by the entry's read-only attribute

#### Scenario: Deleting or changing one hardlinked mirror path does not weaken protection on a sibling sharing the same content
- **WHEN** two different mirror paths were both placed via a hardlink to the same content (deduplicated), and a backup run then deletes or changes one of those mirror paths
- **THEN** the other, still-live mirror path remains read-only afterward
