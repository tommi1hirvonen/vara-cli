## Why

The manifest database (`.vara/profile.db`) carries no schema version. Migrations are detected by inspecting the table definition (the `COLLATE NOCASE` check), which does not scale past one migration, and a build that opens a database written by a newer build would read and write it without noticing. A stable 1.0 needs both a way to order future migrations and a guard against downgrades. After 1.0 a database that predates versioning cannot be told apart from a new one, so the version belongs in 1.0.

## What Changes

- Record a single integer schema version in the database header. A new database is stamped with the current version, 1.
- A database with tables and no recorded version is treated as version 1 and stamped on its first writable open. Databases from before the `COLLATE NOCASE` fix are brought to the version 1 schema first.
- A database whose version is newer than the build supports is refused by every command, read-only ones included, with a hard error (exit 1) and without modification.
- Future migrations run as ordered steps, each committed together with its version bump.
- The manifest's version is also the version of the target's on-disk layout (`.vara/versions`, `.vara/tmp`, content naming). The layout has no separate marker.

## Capabilities

### New Capabilities
- `manifest-versioning`: the recorded schema version of a profile's manifest, how unversioned and older databases are brought to it, and refusal of newer ones.

### Modified Capabilities

None. No existing spec describes the manifest's storage.

## Impact

- `src/Vara.Engine/Storage/ManifestSchema.fs` (version read, stamp, step runner) and `SqliteManifest.fs` (`openAt`, `openReadOnly`).
- A new error case surfaced by the CLI error reporting for a too-new database.
- Tests: `SqliteManifestTests` (reuses its `legacySchema` fixture) and one process test for the exit code.
- No change for users: existing databases are stamped silently.
