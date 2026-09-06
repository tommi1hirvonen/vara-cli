## Why

An earlier fix (`fix-current-state-case-sensitivity`) made `SqliteSnapshotRepository.GetCurrentState()`'s
in-memory result dictionary case-insensitive (`StringComparer.OrdinalIgnoreCase`), but the SQL query
feeding it still groups with `GROUP BY relative_path`, which SQLite evaluates under the default
case-sensitive `BINARY` collation. When the `file_versions` table holds rows for the same logical
path recorded under two different casings (for example, an old pre-fix run, or a casing-only rename
that was itself recorded as two separate rows), the SQL groups them into two separate "latest row"
results instead of one. Both rows are then inserted into the case-insensitive dictionary keyed by
`relative_path`, so whichever row SQLite happens to return last silently wins - with no `ORDER BY`
to make that deterministic. The surviving entry, and therefore which version's hash future
incremental diffing compares against, becomes nondeterministic across otherwise-identical runs.

## What Changes

- `GetCurrentState()`'s query resolves the "latest row per logical path" grouping case-insensitively
  at the SQL level (for example via `COLLATE NOCASE` on the grouping/join columns, or by grouping in
  application code instead), so at most one row survives per case-insensitive path regardless of
  how many distinct casings were historically recorded for it.
- The choice of which casing's row is authoritative when duplicates exist is made deterministic
  (for example, the most-recently-recorded row wins, consistent with existing `MAX(id)` intent).

## Capabilities

### New Capabilities

(none)

### Modified Capabilities

- `backup-execution`: the "Incremental change detection" requirement is clarified so that resolving
  a path's current recorded state is deterministic and case-insensitive even when multiple casings
  of the same logical path exist across historical manifest rows, not only for a single casing-only
  rename event.

## Impact

- **Code**: `src/Vara.Infrastructure/Snapshots/SqliteSnapshotRepository.cs` (`GetCurrentState()`,
  and the sibling queries at lines ~296-324 and ~332-350 that share the same `GROUP BY relative_path`
  pattern, for consistency).
- **Tests**: `tests/Vara.Infrastructure.Tests/Snapshots/SqliteSnapshotRepositoryTests.cs` - a
  regression test seeding two differently-cased rows for the same logical path and asserting
  deterministic, repeatable resolution.
- No on-disk schema changes; purely a query/lookup correctness fix.
