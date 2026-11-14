# manifest-versioning Specification

## Purpose

Gives the manifest database a recorded schema version, so that databases written before versioning keep working, later migrations apply in order, and a build never opens a manifest written by a newer one.

## Requirements

### Requirement: The manifest records a schema version
The system SHALL record an integer schema version in each profile's manifest database. A database created by the system SHALL be stamped with the current version, which is 1.

#### Scenario: New database is stamped
- **WHEN** a backup creates the manifest for a profile whose target has none
- **THEN** the manifest records schema version 1

### Requirement: An unversioned manifest is version 1
The system SHALL treat a manifest that holds tables but no recorded version as schema version 1, and SHALL record version 1 in it on its first writable open. Opening it SHALL NOT change its contents otherwise. A manifest from before the case-insensitive path fix SHALL first be brought to the version 1 schema, then stamped.

#### Scenario: Unversioned manifest is stamped on a backup
- **WHEN** a backup runs for a profile whose manifest has tables and no recorded version
- **THEN** the run succeeds, the history is unchanged, and the manifest records schema version 1

#### Scenario: Pre-collation manifest is migrated and stamped
- **WHEN** a backup runs for a profile whose manifest predates the case-insensitive path fix
- **THEN** the manifest is migrated to the current schema, keeps every recorded file version, and records schema version 1

#### Scenario: Read-only command leaves an unversioned manifest as it is
- **WHEN** a read-only command such as `vara history` runs for a profile whose manifest has no recorded version
- **THEN** the command works and the manifest file is not modified

### Requirement: A newer manifest is refused
The system SHALL refuse to open a manifest whose recorded version is newer than the highest version it supports, for every command including read-only ones. The refusal SHALL name the manifest's version and the highest supported version, SHALL exit with the hard-error status, and SHALL NOT modify the manifest, the mirror or the content store.

#### Scenario: Backup refuses a newer manifest
- **WHEN** a user runs `vara backup <profile>` and the manifest records a version newer than supported
- **THEN** the system reports both versions on standard error, exits with status 1, and changes nothing in the target

#### Scenario: Read-only command refuses a newer manifest
- **WHEN** a user runs `vara history <profile>` and the manifest records a version newer than supported
- **THEN** the system reports both versions on standard error, exits with status 1, and leaves the manifest file unmodified

### Requirement: Older versions migrate in order
When the manifest records a version older than the current one, the first writable open SHALL apply each missing migration in order, and each migration SHALL take effect together with the version it produces or not at all. A failed migration SHALL leave the manifest at the version it had before.

#### Scenario: Interrupted migration leaves the previous version
- **WHEN** a migration step fails while a manifest at an older version is being upgraded
- **THEN** the manifest records the version it had before the step and holds the data it held before it
