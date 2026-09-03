# backup-browsing Specification

## Purpose

Lets a user navigate a profile's backed-up mirror like a filesystem - listing what a directory
contains, at a point in time or now, including items that were later deleted - so a user who
does not already know a file's exact path can still find and recover it.

## Requirements

### Requirement: One-level directory listing
The system SHALL provide a command that lists the immediate (one-level, non-recursive) contents
of a directory within a profile's mirror, presented as an aligned table with column headers,
distinguishing files from subdirectories. By default the listing reflects the current, live
state of the directory (files and subdirectories not deleted as of the most recent snapshot).

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

### Requirement: Point-in-time directory listing
The system SHALL support listing a directory's contents as they existed as of a given date,
instead of the current state, so a user can see what a directory looked like at a specific point
in the past. An entry not yet added, or already deleted, as of that date SHALL NOT appear in a
point-in-time listing.

#### Scenario: Listing a directory as it looked on a past date
- **WHEN** a user requests a directory's contents as of a date prior to the current moment
- **THEN** the system displays only the entries that existed in that directory at that date,
  excluding entries added after that date and entries already deleted by that date

### Requirement: Including deleted entries in a directory listing
The system SHALL support including entries that were deleted from a directory, in addition to its
live contents, so a user can find something to restore without already knowing it was deleted.
A deleted entry SHALL be visually marked as deleted, distinguishing it from live entries, and
deleted entries SHALL be interleaved with live entries rather than grouped into a separate
section.

#### Scenario: Including deleted entries in the current listing
- **WHEN** a user requests a directory's current contents with deleted entries included
- **THEN** the system displays both the directory's live entries and any entry deleted from that
  directory, with each deleted entry visually marked as deleted and interleaved among the live
  entries rather than listed separately

#### Scenario: Including deleted entries in a point-in-time listing
- **WHEN** a user requests a directory's contents as of a given date with deleted entries
  included
- **THEN** the system displays the entries that existed in that directory at that date, together
  with any entry already deleted at or before that date, each visually marked as deleted

#### Scenario: Entry moved out of the listed directory
- **WHEN** a directory listing includes deleted entries, and a file that once lived in the listed
  directory was subsequently moved to a location outside that directory rather than deleted
- **THEN** the system marks that entry as moved, distinguishing it from an entry that was deleted
  outright

#### Scenario: Subdirectory whose contents are entirely deleted
- **WHEN** a directory listing includes deleted entries, and a subdirectory of the listed
  directory has no live entries remaining but previously contained tracked files
- **THEN** the system still lists that subdirectory, visually marked as deleted, so the user can
  descend into it to find its recoverable contents

### Requirement: Recently deleted report
The system SHALL provide a command that reports files deleted from a profile, most recently
deleted first, optionally scoped to a subtree rather than the whole profile, so a user who does
not remember where a missing file lived can still find it.

#### Scenario: Listing recently deleted files across a profile
- **WHEN** a user requests the profile's recently deleted files without scoping to a specific
  directory
- **THEN** the system displays every currently deleted file tracked anywhere in the profile, most
  recently deleted first, as a row of an aligned table

#### Scenario: Scoping the recently deleted report to a subtree
- **WHEN** a user requests the recently deleted report scoped to a specific directory path
- **THEN** the system displays only deleted files whose recorded path falls under that directory

#### Scenario: No deleted files found
- **WHEN** a user requests the recently deleted report and no file currently qualifies as
  deleted within the requested scope
- **THEN** the system reports that no deleted files are recorded, rather than displaying an empty
  table
