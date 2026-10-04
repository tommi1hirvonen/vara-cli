## 1. Snapshot change query and aggregation

- [x] 1.1 Add a repository query for file-version events by snapshot id and normalized directory prefix, and verify SQLite repository tests cover snapshot isolation, prefix scoping, and empty results.
- [x] 1.2 Add application-layer snapshot-change aggregation for directory ancestors, event-kind totals, and optional file details; verify tests cover rollups, links, moves, path scopes, and no matching events.

## 2. CLI report

- [x] 2.1 Add and register `vara snapshot <id> [directory]` with profile/config resolution and `--files`; verify command tests cover option parsing, unknown snapshots, empty scopes, and read-only behavior.
- [x] 2.2 Render the snapshot summary and sparse directory tree with source-root labels and opt-in file paths; verify presenter tests cover event counts, source labels, move origins, and safe path rendering.

## 3. End-to-end verification

- [x] 3.1 Add or extend integration coverage for a snapshot containing changes in multiple directories and verify scoped and unscoped CLI reports match the recorded events.
- [x] 3.2 Run the focused CLI, application, infrastructure, and integration tests for snapshot history and verify the new command is registered in CLI help.
