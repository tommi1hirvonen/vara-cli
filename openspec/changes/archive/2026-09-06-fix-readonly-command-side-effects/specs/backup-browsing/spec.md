## MODIFIED Requirements

### Requirement: One-level directory listing
The system SHALL provide a command that lists the immediate (one-level, non-recursive) contents
of a directory within a profile's mirror, presented as an aligned table with column headers,
distinguishing files from subdirectories. By default the listing reflects the current, live
state of the directory (files and subdirectories not deleted as of the most recent snapshot).
Listing a directory's contents SHALL NOT itself create the profile's backing storage (its
manifest database or on-disk profile directory) as a side effect; it is a read-only operation.

#### Scenario: Listing a directory's current contents
- **WHEN** a user requests the contents of a directory path that exists in the profile's current
  mirror state
- **THEN** the system displays each immediate file and subdirectory entry as a row of an aligned
  table, without descending into subdirectories

#### Scenario: Directory never tracked
- **WHEN** a user requests the contents of a directory path for which no tracked file's recorded
  path, at any point in history, falls under it
- **THEN** the system reports that no such directory is recorded in the backup, rather than
  displaying an empty table

#### Scenario: Browsing a profile that has never completed a backup run
- **WHEN** a user runs the browse command against a profile whose backing storage does not yet
  exist because it has never completed a backup run
- **THEN** the system reports that no such directory is recorded in the backup, and does not
  create the profile's backing storage as a result of the request
