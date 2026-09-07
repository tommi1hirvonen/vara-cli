## Why

`RetentionEvaluator` buckets the daily and weekly tiers by the snapshot timestamp's UTC calendar date (`UtcDateTime.Date`), but buckets the monthly and yearly tiers by `.Year`/`.Month` read directly off the stored `DateTimeOffset` - which reflects whatever offset was captured at snapshot time, not UTC. For a snapshot near a month, year, or day boundary in a non-zero offset, this means the same snapshot can be assigned to a different calendar period depending on which tier is doing the bucketing, so which snapshot a tier retains as "newest in its bucket" becomes inconsistent and dependent on the machine's local offset at capture time rather than a single, predictable calendar basis. No existing test exercises a non-zero offset, so this inconsistency currently has no coverage.

## What Changes

- Normalize monthly and yearly tier bucketing in `RetentionEvaluator` to use the same UTC calendar-date basis already used by the daily and weekly tiers, instead of the stored offset's `.Year`/`.Month`.
- No change to retention policy configuration (`keep_daily`/`keep_weekly`/`keep_monthly`/`keep_yearly`), to the "always retain the newest completed snapshot" rule, or to the daily/weekly bucketing that is already UTC-based.
- Not a configuration or API change; for profiles whose snapshots carry a non-zero offset near a calendar boundary, which snapshot a tier selects as "newest in bucket" can change to be consistent with the daily/weekly tiers - this is a correctness fix to existing behavior, not a new capability.

## Capabilities

### New Capabilities
(none)

### Modified Capabilities
- `retention-pruning`: the "Tiered retention evaluation" requirement is clarified so all four tiers bucket by the same UTC calendar-date basis, removing the offset-dependent monthly/yearly bucketing.

## Impact

- `src/Vara.Application/Retention/RetentionEvaluator.cs` (monthly/yearly bucket key functions)
- `tests/Vara.Application.Tests/Retention/RetentionEvaluatorTests.cs` (add non-zero-offset coverage)
