## 1. Fix bucket key computation

- [ ] 1.1 Change `RetentionEvaluator`'s monthly bucket key function to build the key from `Timestamp(s).UtcDateTime.Year`/`.Month` instead of the `DateTimeOffset`'s own `.Year`/`.Month`, and verify by inspection that it now mirrors the daily tier's `UtcDateTime.Date` pattern
- [ ] 1.2 Change the yearly bucket key function the same way, building from `Timestamp(s).UtcDateTime.Year`
- [ ] 1.3 Confirm the daily and weekly bucket key functions are unchanged (already UTC-based) and that no other call site reads `.Year`/`.Month` directly off a `DateTimeOffset` snapshot timestamp within `RetentionEvaluator`

## 2. Test coverage

- [ ] 2.1 Add a test with a non-zero-offset snapshot timestamp straddling a UTC/local month boundary (e.g. `2026-02-01T00:30:00+03:00`, UTC calendar date in January) and assert the monthly tier buckets it using the UTC date
- [ ] 2.2 Add a test with a non-zero-offset snapshot timestamp straddling a UTC/local year boundary and assert the yearly tier buckets it using the UTC date
- [ ] 2.3 Add a test asserting that, for the same set of snapshots with non-zero offsets, the daily/weekly and monthly/yearly tiers agree on which snapshot is "newest in its respective bucket" for a period they both cover, verifying no more per-tier inconsistency
- [ ] 2.4 Run `cd src; dotnet test ..\tests\Vara.Application.Tests` (or the equivalent targeted test filter for `RetentionEvaluatorTests`) and verify all tests pass

## 3. Spec alignment

- [ ] 3.1 Verify the updated `retention-pruning` delta spec's new scenario ("Snapshot timestamp near a calendar boundary in a non-zero offset") is satisfied by the implementation and covered by a corresponding test from Task 2
