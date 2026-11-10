## Why

A prune run removes snapshot records from the manifest first and deletes the unreferenced content blobs second, on the stated basis that a crash in between leaves unreferenced blobs and never a referenced blob missing. That basis does not hold across a power loss: the manifest commits in WAL mode with `synchronous = NORMAL`, where a committed transaction can be rolled back by a power failure, while the blob deletions that followed it are already on disk. The manifest would then reference blobs that no longer exist, which loses the history the prune was meant to keep.

## What Changes

- Prune makes the removal of snapshot records durable before it deletes the first blob, so a power loss at any later point cannot bring those records back.
- The manifest file on disk is whole (write-ahead log folded back into it) after the removal, as it already is after a backup run.
- The comment in the prune run that states the ordering guarantee is corrected to name durability as what supports it.

No command-line, output, configuration or schema change.

## Capabilities

### New Capabilities
(none)

### Modified Capabilities
- `retention-pruning`: adds a requirement that the removal of snapshot records is durable before any stored content is removed.

## Impact

- `src/Vara.Engine/Storage/SqliteManifest.fs` (`removeSnapshots`, and a small shared helper with `checkpoint`)
- `src/Vara.Engine/Runs/PruneRun.fs` (comment only)
- `tests/Vara.Engine.Tests/Runs/MaintenanceEngineTests.fs` (new scenario tests)
- Cost: one extra log sync and one WAL checkpoint per prune that removes at least one snapshot. A prune that removes none is unchanged.
