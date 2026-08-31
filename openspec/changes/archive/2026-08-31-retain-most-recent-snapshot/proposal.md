## Why

Under a fully-expiring retention policy (all tier counts at 0, or a policy that otherwise retains nothing), `vara prune` can delete the manifest row for the most recent completed snapshot. "Current row" protection in `SqliteSnapshotRepository.PruneSnapshots` operates per file-version row, not per snapshot: a completed snapshot that recorded zero file changes (e.g. a no-op re-run) has no `file_versions` rows to anchor it, so it is deleted outright even though it is the freshest confirmation that the profile is up to date. In the degenerate case where the live mirror has been fully emptied, no row is protected at all and the entire snapshot history can disappear. Either way, `vara snapshots`/`vara history` output no longer reflects reality: there is no trace that the profile was ever backed up, even though prune's job is only to thin out history, not erase it.

## What Changes

- Guarantee that the most recent completed snapshot for a profile is never eligible for removal by `vara prune`, regardless of what the configured retention policy's tier math would otherwise allow.
- Apply this guarantee even when the policy has all-zero tier counts, and even when the most recent completed snapshot has no live/current `file_versions` rows anchoring it.
- Leave all other retention/eligibility behavior (tiered bucket retention, running/failed snapshot handling, garbage collection) unchanged.

## Capabilities

### New Capabilities
(none)

### Modified Capabilities
- `retention-pruning`: The tiered retention evaluation requirement is amended so the most recent completed snapshot is always retained, independent of tier bucket counts.

## Impact

- `src/Vara.Application/Retention/RetentionEvaluator.cs`: `DetermineRetainedSnapshotIds` (or `DetermineEligibleForRemoval`) must always retain the newest completed snapshot.
- `src/Vara.Infrastructure/Snapshots/SqliteSnapshotRepository.cs`: `PruneSnapshots` behavior is unaffected in mechanism, but will no longer receive the newest snapshot's id in the eligible-for-removal set.
- Tests: `tests/Vara.Application.Tests/Retention/RetentionEvaluatorTests.cs`, `tests/Vara.Infrastructure.Tests/Snapshots/SqliteSnapshotRepositoryTests.cs`.
- No CLI surface or configuration schema changes; no breaking changes to `RetentionPolicy`.
