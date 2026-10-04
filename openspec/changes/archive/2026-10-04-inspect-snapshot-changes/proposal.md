## Why

Snapshot history shows profile-wide totals, which makes a large increase difficult to explain: a user cannot tell whether it came from a newly added source or from changes spread across existing directories. Users need a way to inspect the impact of one snapshot at the directory level without being overwhelmed by a list of every changed file.

## What Changes

- Add a `vara snapshot <id> [directory]` command to inspect one recorded snapshot, optionally scoped to a directory.
- Show sparse per-directory rollups for added, changed, moved, deleted, and linked entries; provide optional changed-path detail with `--files`.
- Make source-root locations identifiable in the report when they correspond to the profile's configured sources.

## Capabilities

### New Capabilities

None.

### Modified Capabilities

- `snapshot-history`: Add a command for inspecting a snapshot's directory-scoped changes and optional file-level details.

## Impact

- CLI command registration, snapshot-history query/application logic, and Spectre.Console presentation.
- Existing snapshot manifest rows provide the change events; no new persistence format or dependency is expected.
