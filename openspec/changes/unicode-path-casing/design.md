## Context

See proposal.md - Why. What an implementer in a fresh session needs:

- The `file_versions.relative_path` column is declared `COLLATE NOCASE` (`SqliteManifest.fs`, `createTables` and `migrateCollation`). SQLite's `NOCASE` folds only ASCII letters; `'Ä' = 'ä' COLLATE NOCASE` is false (verified with SQLite 3, any build).
- `SqliteManifest.currentState` runs one query that joins each row to the highest `id` per `relative_path` (grouped with that collation) and filters `change_kind != 'Deleted'`, then loads the rows into `TrackedPaths`, a `Dictionary` with `StringComparer.OrdinalIgnoreCase`, which folds all letters. So two SQL groups can collapse onto one dictionary key, and `result[path] <- ...` keeps whichever came last. The order the groups come back in is not specified; it is likely the index's code point order (`Äiti` before `äiti`), in which case the stale row wins when the file was renamed from lower case to upper case and the correct one wins by luck in the other direction. This was reasoned, not observed: a test must cover both directions, and the first task confirms which one fails.
- `Diff.run` and the scanner already compare paths with `OrdinalIgnoreCase`; no change there.
- The manifest format is shared with the C# build ("either build can continue a target the other wrote"). Anything that changes how the column is collated or indexed is therefore not free: an index written under one collation and read under another is inconsistent.
- Tests: a module named after the requirement title, a test named after the scenario title (F# double-backtick names). `Incremental change detection` already exists as a module in `tests/Vara.Domain.Tests/Backup/DiffTests.fs` and `tests/Vara.Engine.Tests/Storage/SqliteManifestTests.fs`; add to those and use the exact scenario titles from `specs/backup-execution/spec.md`. `tests/Vara.Testing/SpecCoverage.fs` fails when a main-spec scenario has no test, which applies once this change is synced.

## Goals / Non-Goals

**Goals:**
- Exactly one row decides a path's current state, by highest id, with letters of every script folded the way the file system folds them.
- A deletion applies to the logical path whatever casing it was recorded under.
- The latest row's casing is the key's casing (as the existing comment on `currentState` already says).
- Snapshot expiry uses the same resolution, so a record that is not a path's current one never keeps a snapshot or its content alive.
- No change to the schema, the collation or the indexes.

**Non-Goals:**
- Lookups of one path's history (`versionsOfPath`, `hasPath`, `hasPrefix`) stay ASCII-insensitive in SQL: a user typing `äiti.txt` for a path recorded as `Äiti.txt` is not matched. Fixing that needs either a custom collation or a SQL function; see Open Questions.
- Unicode normalization forms (NFC against NFD). File names are compared as the file system hands them out.

## Decisions

**One private resolver, in code, from the SQL's per-group latest rows.** A private function in `SqliteManifest.fs` runs the query below on a given connection and transaction and returns the winning row of each logical path. The query returns each ASCII-group's latest row including deletions (no `change_kind != 'Deleted'` filter), with the row id and the columns `currentState` needs, ordered by id ascending. The resolver folds the rows in that order into a dictionary keyed with `OrdinalIgnoreCase`: a `Deleted` row removes the key, any other row removes the key and then sets it (remove-then-add, so the stored key takes the latest row's casing, since assigning to an existing key keeps the old spelling). The removal happens before anything else is decided about the row, so a regular row that has no usable hash still clears an older row of another casing; `currentState` then skips such a winner when it builds `TrackedPaths` (the engine cannot place or move it), exactly as it skips it today. Because every row of a path is in some group and each group contributes its highest id, the last row applied for a key is the highest id over all its casings, which is the "most recently recorded row" the spec asks for.
Alternatives considered: (a) a custom collation that overrides `NOCASE` (fixes every query, including history lookups, but changes how existing indexes order rows and makes a target written by the C# build inconsistent: rejected for this change, kept as the open question); (b) a normalized key column (schema change, migration, needs the C# build to maintain it); (c) keeping the SQL filter and only picking the highest id in code (does not fix the deletion scenario, because a deleted newer row would be filtered out and an older row of the other casing would remain).

**`currentState` and `removeSnapshots` share the resolver.** `currentState` maps the winners that are not deletions to `TrackedPaths`. `removeSnapshots` takes the ids of the winners that are not deletions as the rows it must keep, in place of its own ASCII-grouped subquery. A prune therefore agrees with a backup about which row is current. Without this, a path recorded as live `Äiti.txt`, live `äiti.txt` and then deleted `äiti.txt` would keep its first row (the latest of its ASCII group, not a deletion) for ever, and with it an expired snapshot and the blob.
The keep set can hold every path ever recorded, which is too many ids to spell into a statement as the list of snapshot ids is. Inside the removal transaction, create a `TEMP` table `live_rows(id INTEGER PRIMARY KEY)`, insert the ids with one prepared statement, delete with `id NOT IN (SELECT id FROM live_rows)`, and drop the table before the commit. The early return for an empty id list stays untouched, so a prune that removes nothing does not pay for the resolver.

**Keep it one query text.** The resolver is the only place with the grouped query. No new public function and no change to `TrackedPaths`.

**Test the failing cases first.** Rows written through `SqliteManifest.record` for both directions of the rename and for the deletion scenario, asserted on `currentState`; one end-to-end test in `tests/Vara.Engine.Tests/Runs/BackupRunTests.fs` that runs three backups around a case-only rename with an edit and asserts the third copies nothing (use the existing helpers there); and prune tests in `tests/Vara.Engine.Tests/Runs/MaintenanceEngineTests.fs` (it already drives `removeSnapshots`) for the two `retention-pruning` scenarios, asserting which snapshots and blobs remain. Confirm the lower-to-upper direction and the deletion prune scenario fail on the current code.

## Risks / Trade-offs

- [The query returns deleted paths' latest rows too, so a manifest with many deleted paths reads more rows] → Rows are streamed once into a dictionary, as before; the volume is bounded by the number of distinct paths ever recorded. If this is ever measurable, a covering index on `(relative_path, id)` already exists for the grouping.
- [Prune now resolves every path and writes the keep set to a temp table] → Prune is an occasional manual command and the work is one pass over the latest rows; a prune that removes no snapshot returns before any of it.
- [`removeSnapshots` keeps rows by id instead of by the SQL subquery, so a mistake in the resolver now also decides what prune deletes] → The two prune scenarios and the existing removal tests in `MaintenanceEngineTests.fs` pin both directions: the current row is never removed, and a non-current row never protects its snapshot.
- [History lookups remain inconsistent with the state for non-ASCII casings] → Stated non-goal and open question; it affects `history`, `restore` and `browse` path matching, not backups.
- [Two ASCII groups of one logical path both survive in the table] → Harmless: the resolution above reads them as one.

## Open Questions

- Whether to follow up with a Unicode-aware collation or SQL function for the history lookups, and what compatibility with the C# build the author still requires. It does not change this change's specs or tasks.
