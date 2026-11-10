## ADDED Requirements

### Requirement: Restored content is verified against its recorded hash
The system SHALL verify, for every file a restore writes, that the content it read from the store has the content hash recorded for the version being restored, and SHALL do so before the restored file replaces any existing file at the destination. WHEN the content does not match, the system SHALL NOT write that file to its destination: the destination SHALL be left exactly as it was before the restore was attempted, no partial or temporary file SHALL remain beside it, and the file SHALL be reported as failed with a reason stating that the stored content does not match what was recorded for it. A single-file restore that fails this way SHALL report an error and exit with the hard-error status. A directory restore SHALL continue with its remaining files and SHALL end as a partial failure listing the file, as it does for any other per-file failure. Content that matches SHALL be restored exactly as before, with no additional output.

#### Scenario: Stored content does not match its recorded hash
- **WHEN** a user restores a single file whose stored content has been damaged, so that its hash differs from the one recorded for the version, and the destination already holds a file
- **THEN** the system reports an error naming the file and stating that the stored content does not match what was recorded, exits with the hard-error status, leaves the existing destination file unchanged, and leaves no temporary file beside it

#### Scenario: One damaged file in a directory restore
- **WHEN** a user restores a directory in which one file's stored content does not match its recorded hash and the other files' content does
- **THEN** the system restores the other files, does not write the damaged file, lists it as failed with the mismatch reason, and exits with the partial-failure status
