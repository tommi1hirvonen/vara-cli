## Why

Snapshot change trees currently print all affected directories and render their per-kind counts without visual distinction. Large, deeply nested snapshots can overwhelm the console, making it harder to spot important change kinds and focus on the part of the tree a user needs.

## What Changes

- Color each directory's added, changed, moved, deleted, and linked counts using the CLI's established change-kind palette.
- Add an optional `--depth <n>` limit for the snapshot directory tree while preserving unlimited output when the option is omitted.
- At the depth boundary, show how many deeper affected directories are omitted; visible ancestor rollups continue to include changes from omitted descendants.

## Capabilities

### New Capabilities

None.

### Modified Capabilities

- `snapshot-history`: Specify change-kind coloring for directory rollups and optional depth-limited tree output with an omitted-directory indicator.

## Impact

- Snapshot command option parsing and CLI tree presentation.
- Snapshot command and presenter tests; no data model, persistence, or dependency changes are expected.
