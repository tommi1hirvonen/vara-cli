## 1. Schema migration

- [ ] 1.1 Add a schema-version-guarded migration that recreates `file_versions` with `relative_path TEXT COLLATE NOCASE`, copies existing rows, and swaps the table in, and verify a test seeding a pre-migration database fixture confirms all rows survive with content intact after migration
- [ ] 1.2 Update the table's `CREATE TABLE` definition (for brand-new databases) to declare `COLLATE NOCASE` on `relative_path` directly, and verify a fresh database's schema reflects the new collation

## 2. Query correctness

- [ ] 2.1 Verify (via a seeded test with two differently-cased rows for the same logical path) that `GetCurrentState()` returns exactly one entry for that path, deterministically the most-recently-recorded one, across repeated calls
- [ ] 2.2 Apply the same verification to `GetCurrentState(asOf)` and `GetDeletedFiles`/any other query sharing the `GROUP BY relative_path` pattern
- [ ] 2.3 Add a regression test reproducing the original bug report (two casings, nondeterministic winner) and confirm it now passes deterministically across multiple runs

## 3. Verification

- [ ] 3.1 Run `openspec validate --change fix-current-state-group-by-collation --strict` and confirm it passes
- [ ] 3.2 Run the full `Vara.Infrastructure.Tests` and `Vara.Application.Tests` suites and confirm no existing tests regress
