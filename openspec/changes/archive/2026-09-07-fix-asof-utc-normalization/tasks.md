## 1. Fix the offset-naive `asOf` comparison

- [x] 1.1 In `SqliteSnapshotRepository.GetStateAsOf`, bind the `$asOf` SQL parameter using
  `ToIso(asOf.ToUniversalTime())` instead of `ToIso(asOf)`, and verify by inspection that the bound
  value now always carries a `+00:00` offset regardless of the caller's input offset.
- [x] 1.2 In `SqliteSnapshotRepository.GetTombstones`, apply the same `ToIso(asOf.ToUniversalTime())`
  change to its `$asOf` binding.

## 2. Add regression coverage for non-UTC offsets

- [x] 2.1 In `SqliteSnapshotRepositoryTests.cs`, add a test that records a `file_versions` row at a
  known UTC instant, then calls `GetStateAsOf` with an equivalent `asOf` expressed at a non-zero
  offset (e.g. `+03:00`) straddling the boundary the finding describes (a row recorded shortly
  after the true UTC instant must be excluded, one recorded shortly before must be included), and
  verify the returned state matches what a `TimeSpan.Zero`-offset `asOf` for the same instant
  returns.
- [x] 2.2 Add the equivalent test for `GetTombstones` with a non-zero-offset `asOf`, verifying
  deleted-entry inclusion/exclusion matches the same-instant `TimeSpan.Zero` result.
- [x] 2.3 Add a test asserting `GetStateAsOf`/`GetTombstones` agree with
  `FindVersionAsOf`/`SnapshotHistoryService.ResolveVersion` for the same non-zero-offset `asOf`
  instant, guarding against `browse` and `restore`/`show` disagreeing again.

## 3. Verify

- [x] 3.1 Run `dotnet test tests/Vara.Infrastructure.Tests` (or the full solution test run per the
  project's build convention) and confirm all tests, including the new ones, pass.
