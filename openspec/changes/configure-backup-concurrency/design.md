## Context

See proposal.md - Why. Both `BackupPlanner` and `BackupExecutor` already accept a `maxDegreeOfParallelism` constructor parameter; when it is `0` (the default), each falls back independently to `Environment.ProcessorCount` (`BackupPlanner.cs:26`, `BackupExecutor.cs:26`). `BackupPipeline.Run` always constructs both with no explicit value (`BackupPipeline.cs:52,57`), so the parameter is currently unreachable from anywhere outside a test.

`Profile` (`Vara.Core.Configuration.Profile`) already has one precedent for this shape of optional, per-profile tuning: `Retention` (`RetentionPolicy?`). `YamlProfileConfigLoader` parses its snake_case YAML keys (`keep_daily`, etc.) into the record's PascalCase fields and raises a profile-facing validation error - naming the profile, field, and offending value - for a value that fails to parse or is out of range, rather than letting an exception escape unhandled. `RetentionPolicy`'s own constructor also independently validates (`ArgumentOutOfRangeException` for a negative count) as a defensive invariant.

## Goals / Non-Goals

**Goals:**
- Make the two existing (but currently unreachable) concurrency knobs settable from profile configuration, following the `RetentionPolicy` precedent's shape and validation pattern.
- Give the transfer stage (`BackupExecutor`) a new unconfigured default of `1`, suited to the common NVMe-source/HDD-target workload, without changing the scan stage's (`BackupPlanner`) existing default.
- Keep the new default isolated behind a single named constant so it can be revised later from measurement without a design change - mirroring the original mitigation that introduced the parameter in the first place.

**Non-Goals:**
- No automatic hardware or same-volume detection. This change is about making manual configuration reachable and re-defaulting the transfer stage; detecting media type or source/target volume identity is a separate, harder problem left for a future change.
- No CLI flag surface for concurrency - profile-config only, consistent with how retention is configured today.
- No change to `BackupPlanner`'s algorithm or its own default value - only its reachability changes.

## Decisions

1. **New `ConcurrencySettings` record, mirroring `RetentionPolicy`'s shape.** A nullable `Concurrency` property on `Profile`, with two independent nullable `int?` fields: `ScanConcurrency` and `TransferConcurrency`. `null` means "not configured" (distinct from a validated positive integer). *Alternative considered:* using the constructors' existing `0`-means-default sentinel directly on the record - rejected because `0` is also a value a user might mistakenly write meaning "unlimited" or "disabled", and a nullable field is self-documenting and matches the existing `RetentionPolicy`/loader split between "value present" and "value valid".

2. **`BackupPipeline` translates "not configured" back to each component's own existing sentinel**, i.e. `profile.Concurrency?.ScanConcurrency ?? 0` and `profile.Concurrency?.TransferConcurrency ?? 0`, passed straight into `BackupPlanner`/`BackupExecutor`'s existing constructors. Each class's own fallback expression remains the single source of truth for its default value - `BackupPipeline` never duplicates or re-derives what that default number is.

3. **`BackupExecutor`'s fallback expression changes from `Environment.ProcessorCount` to a private named constant (`DefaultTransferConcurrency = 1`).** `BackupPlanner`'s fallback expression is untouched. This keeps "easy to change later" literal: revising the transfer default is a one-line constant edit, not a change to a call site or a design decision.

4. **Validation happens in `YamlProfileConfigLoader`**, alongside retention parsing, using the same non-negative-whole-number-style helper pattern as retention's `GetOptionalRetentionCount` (adapted to require strictly positive values, matching the spec's "positive whole number" wording - a configured concurrency of `0` is meaningless, unlike a retention count of `0` which legitimately means "keep none of this tier"). `ConcurrencySettings`'s own constructor additionally throws `ArgumentOutOfRangeException` for a non-positive value, mirroring `RetentionPolicy`'s belt-and-suspenders invariant.

5. **`docs/config-sample.yml` gets a `concurrency:` section**, placed alongside the existing `retention:` section on the sample's `files` profile, following that section's established convention of inline `#` comments explaining optionality and defaults directly in the sample rather than in prose elsewhere. This is the only user-facing documentation surface for profile configuration today, so it is the natural (and precedented) place to make the new section discoverable.

## Risks / Trade-offs

- [Risk] The transfer stage's new default of `1` is a reasoned choice, not an empirically measured one - the original design's deferred "tune empirically against real hardware" item was never resolved for either the fast or slow case → Mitigation: isolate it behind one named constant (Decision 3) so a future measurement-driven change is a one-line edit.
- [Trade-off] Splitting scan and transfer concurrency into two independent settings adds profile-config surface area, working against "fewer knobs to learn" → Mitigation: both remain optional with sensible defaults; a user who never touches either sees only the new transfer default, and never needs to understand the two-stage pipeline to use Vara.
- [Trade-off] The new transfer default of `1` improves the common NVMe-source/HDD-target case but slows the NVMe-to-NVMe (or SSD-to-SSD) case relative to today's unreachable `Environment.ProcessorCount` fallback, until such a user discovers and sets `transfer_concurrency` explicitly → Accepted per explicit product direction: the common case wins by default, and the minority case has a documented, explicit override rather than no override at all (today's state).

## Migration Plan

No data migration. Existing `profiles.yml` files without a `concurrency` section keep loading and running; the scan stage's behavior is unchanged, while the transfer stage's *effective* concurrency changes from `Environment.ProcessorCount` to `1` for every profile that does not explicitly opt into a higher value - this is an intentional behavior change for existing profiles, not a configuration-format break. No special rollback path is needed beyond removing the `concurrency` section, which is equivalent to never having set it.

## Open Questions

- The transfer stage's hardcoded default of `1` may warrant revision once measured against real HDD hardware; per the Decisions above, that would be a one-line constant change requiring no spec or task-breakdown update, since no spec in this change mandates a specific numeric default.
