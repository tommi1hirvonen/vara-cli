## ADDED Requirements

### Requirement: Stored content is complete and durable under its content name
The system SHALL NOT make content available in the version store under its content-derived name until all of that content has been written, and SHALL ensure the content has reached persistent storage, not only the operating system's cache, before it takes that name. As a result, an abrupt loss of power during or after a backup run SHALL NOT leave a blob under a content name whose stored bytes are fewer than the content it is named for because the write had not yet reached the disk.

#### Scenario: Store interrupted while the content is being written
- **WHEN** reading a source file or writing its content to the version store fails partway through
- **THEN** no blob exists in the version store under that content's name, and the file is reported as failed

#### Scenario: Content reaches persistent storage before it takes its name
- **WHEN** a backup run stores a file's content in the version store
- **THEN** the content has been flushed to persistent storage before it appears in the version store under its content-derived name

### Requirement: A damaged stored blob is repaired when its content is stored again
WHEN a backup run stores content whose content-derived name is already present in the version store, and the size of the present blob differs from the size of the content just read, the system SHALL replace the present blob's content with the complete content and SHALL NOT report the file as failed. The replacement SHALL be visible through every name that refers to the blob, including mirror entries that are hard links to it, and through every later restore. A present blob whose size equals the size of the content SHALL be left untouched. WHEN several files with the same content are stored in one run, the repair SHALL happen once and no file SHALL fail or be written with incomplete content because of it.

#### Scenario: Present blob is shorter than the content it is named for
- **WHEN** a backup run stores content whose name is present in the version store as a blob shorter than the content
- **THEN** the blob holds the complete content afterwards, the file is recorded in the manifest as it would be for any stored file, and no failure is reported

#### Scenario: Mirror entries linked to a damaged blob see the repaired content
- **WHEN** a mirror entry is a hard link to a blob that is shorter than the content it is named for, and a backup run stores that content again
- **THEN** that mirror entry holds the complete content after the run

#### Scenario: Present blob of the expected size is left untouched
- **WHEN** a backup run stores content whose name is present in the version store as a blob of the same size as the content
- **THEN** the present blob is not rewritten and the temporary copy of the content is discarded

#### Scenario: Several files with the same damaged content are stored in one run
- **WHEN** a backup run stores several files with identical content at the same time, and the version store holds a blob for that content that is shorter than the content
- **THEN** the blob holds the complete content afterwards, every one of the files is recorded without failure, and every mirror entry for them holds the complete content
