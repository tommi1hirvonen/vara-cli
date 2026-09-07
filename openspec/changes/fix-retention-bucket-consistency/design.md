## Context

See proposal.md - Why. `RetentionEvaluator.DetermineRetainedSnapshotIds` computes a separate bucket key per tier: daily and weekly already normalize through `Timestamp(s).UtcDateTime.Date`, while monthly and yearly build a `DateTime` directly from `Timestamp(s).Year`/`.Month` - a `DateTimeOffset` property that reflects the offset stored with that snapshot's timestamp, not UTC. No existing test uses a non-zero offset, so the divergence has shipped uncaught.

## Goals / Non-Goals

**Goals:**
- Make all four tiers derive their calendar bucket from the same UTC calendar-date basis.
- Preserve every other part of the algorithm: the "always retain newest completed snapshot" rule, per-tier bucket counts, and the eligible-for-removal computation.

**Non-Goals:**
- Not introducing per-profile or configurable timezone bucketing. The retention policy has no notion of a profile timezone today, and adding one is a separate, larger feature that isn't needed to fix the inconsistency between tiers.
- Not changing how a snapshot's timestamp is captured or stored (`Snapshot.CompletedAt`/`StartedAt` remain `DateTimeOffset` as-is).

## Decisions

**Normalize monthly/yearly bucketing to UTC date, matching daily/weekly, rather than making the basis configurable.** Two tiers already use UTC; making the other two match is the minimal change that removes the inconsistency the finding identified. A configurable timezone basis was considered and rejected for this change: it would require a new per-profile setting, a migration story for existing configs, and a decision about how it interacts with each tier - none of which the original finding calls for. It also wouldn't fix the actual bug (two tiers disagreeing with each other), only relocate it to be configurable disagreement instead of hardcoded disagreement.

**Bucket key implementation**: build the monthly key as `new DateTime(Timestamp(s).UtcDateTime.Year, Timestamp(s).UtcDateTime.Month, 1)` and the yearly key as `new DateTime(Timestamp(s).UtcDateTime.Year, 1, 1)`, mirroring the existing daily key's `Timestamp(s).UtcDateTime.Date` pattern exactly, so all four tiers read from `UtcDateTime` and none read `.Year`/`.Month` directly off the offset value.

## Risks / Trade-offs

- [For a profile whose snapshots carry a non-zero offset near a month/year boundary, which snapshot a monthly/yearly tier retains as "newest in bucket" can change after this fix, since it now agrees with the daily/weekly tiers instead of the previous offset-based reading] → This is the intended correctness fix; the previous behavior was an unintentional inconsistency with no test coverage, not a documented guarantee. No data is lost - eligible-for-removal snapshots are only pruned on an explicit, confirmed `vara prune` run, so this change only affects which snapshots become eligible, not any automatic deletion.
- [Machines running in UTC (offset zero) see no behavior change at all] → Confirms the fix is targeted precisely at the reported inconsistency rather than altering the common case.
