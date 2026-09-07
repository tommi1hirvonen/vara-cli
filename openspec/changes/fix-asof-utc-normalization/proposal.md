## Why

`vara browse --at`'s point-in-time queries (`SqliteSnapshotRepository.GetStateAsOf` /
`GetTombstones`) bind the parsed `--at`/`--since` value to SQL via `ToIso(asOf)`, which formats
the `DateTimeOffset` with whatever offset it carries (e.g. `+03:00` for a locally-resolved time)
instead of normalizing to UTC. `recorded_at` rows are always written in UTC (`+00:00`), so SQLite's
lexicographic TEXT comparison (`recorded_at <= $asOf`) compares clock digits rather than instants,
silently returning wrong results whenever the caller's local offset isn't zero. `restore`/`show`/
`diff` resolve the same kind of `--at` value through `SnapshotHistoryService.ResolveVersion` /
`SqliteSnapshotRepository.FindVersionAsOf`, which parse all rows into `DateTimeOffset` and compare
in memory - correctly, offset-aware - so `browse` and `restore` can disagree about which version
was current at the same `--at` instant. This already-specified point-in-time behavior (see
`backup-browsing`'s "Point-in-time directory listing" requirement) is not being honored correctly
by the SQL-side implementation; fixing it does not change what the system is supposed to do.

## What Changes

- Normalize the `asOf` parameter to UTC before formatting it for the SQL comparison in
  `SqliteSnapshotRepository.GetStateAsOf` and `GetTombstones`, so `recorded_at <= $asOf` compares
  like-for-like UTC instants regardless of the offset the caller's `DateTimeOffset` carries.
- Add regression coverage with a non-zero-offset `asOf` (e.g. `+03:00`) exercising both
  `GetStateAsOf`/`GetTombstones` and `FindVersionAsOf`/`ResolveVersion` for the same instant, to
  confirm they agree and to guard against this regressing.

## Capabilities

### New Capabilities
(none)

### Modified Capabilities
(none - this corrects an implementation bug in `backup-browsing`'s existing "Point-in-time
directory listing" requirement; the requirement's specified behavior, listing entries as they
existed as of a given instant, does not change)

## Impact

- `src/Vara.Infrastructure/Snapshots/SqliteSnapshotRepository.cs`: `GetStateAsOf` and
  `GetTombstones` (the two call sites binding `$asOf` for the SQL `recorded_at <= $asOf` filter).
- `tests/Vara.Infrastructure.Tests/Snapshots/SqliteSnapshotRepositoryTests.cs`: new regression
  test(s) covering a non-UTC `asOf` offset.
- No CLI, storage schema, or public API changes. No behavior change for callers already passing
  UTC `DateTimeOffset` values (e.g. anything already using `Zero` offset in existing tests).
