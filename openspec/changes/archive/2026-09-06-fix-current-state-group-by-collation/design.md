## Context

`SqliteSnapshotRepository.GetCurrentState()` (and its `GetCurrentState(asOf)`/`GetDeletedFiles`
siblings) resolve "the latest row per path" with `SELECT relative_path, MAX(id) AS max_id FROM
file_versions GROUP BY relative_path`. SQLite groups `TEXT` values under the column's declared
collation, which defaults to `BINARY` (case-sensitive) unless declared otherwise - `relative_path`
has no `COLLATE NOCASE` declaration in the schema. The in-memory result dictionary is already
`OrdinalIgnoreCase` (fixed by `fix-current-state-case-sensitivity`), but that only controls what
happens *after* SQL has already produced however many groups it produced. See proposal.md for the
concrete failure mode.

## Goals / Non-Goals

**Goals:**
- Make "the current state of a logical path" resolve to exactly one row, deterministically, even
  when historical rows exist under more than one casing.
- Keep the fix localized to read paths (`GetCurrentState`, `GetCurrentState(asOf)`,
  `GetDeletedFiles`, `Prune`'s equivalent subquery) without touching how rows are written.

**Non-Goals:**
- Retroactively merging/deduplicating existing multi-casing rows in already-created databases -
  out of scope; the fix only needs to make *resolution* deterministic, not rewrite history.
- Changing `file_versions`' on-disk schema or adding a migration - out of scope if avoidable.

## Decisions

- **Prefer a schema-level `COLLATE NOCASE` on `file_versions.relative_path`** over rewriting the
  grouping in application code, since every query that groups/joins on `relative_path` (there are
  at least four in this file) inherits the fix for free, rather than needing the same case-folding
  logic repeated at each call site.
  - Alternative considered: keep `BINARY` collation and instead do the "pick latest per
    case-insensitive path" grouping in C# after fetching all matching rows. Rejected: pulls
    potentially every historical row for a path over the wire/into memory just to fold client-side,
    where the existing SQL already does the equivalent work efficiently once collation is fixed.
  - Alternative considered: add `COLLATE NOCASE` inline on each `GROUP BY`/`JOIN` clause instead of
    the column definition (e.g. `GROUP BY relative_path COLLATE NOCASE`). Viable without a schema
    change, but every current and future query touching this column would need to remember to add
    it - a schema-level default is harder to forget.
- **Schema change requires a migration path**: existing `profile.db` files were created without
  `COLLATE NOCASE`. SQLite does not support altering a column's collation in place. The migration
  creates a replacement `file_versions` table with the corrected collation, copies existing rows,
  and swaps it in - guarded by the existing schema-version mechanism the repository already uses
  for prior changes.
- **Tie-breaking for duplicate casings remains `MAX(id)`** (most-recently-recorded row wins),
  unchanged in spirit from today's intent - only the grouping key's collation changes, not which
  row within a group is chosen.

## Risks / Trade-offs

- [A schema migration touching an existing table risks data loss on a botched migration] →
  Mitigated by running the migration inside the same transaction/connection pattern already used
  for schema upgrades, and by covering it with a test that seeds a pre-migration-shaped database
  fixture and asserts all rows survive with content intact.
- [Existing databases with genuine duplicate-casing rows will, post-migration, have those rows
  silently start resolving to one winner where they previously might have alternated] → This is the
  intended fix (nondeterminism was already a bug), called out explicitly in the proposal so it's a
  known, accepted behavior change rather than a silent side effect.

## Open Questions

(none)
