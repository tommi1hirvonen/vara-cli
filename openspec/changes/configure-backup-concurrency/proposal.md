## Why

`BackupPlanner` and `BackupExecutor` already accept a `maxDegreeOfParallelism` constructor parameter, added specifically so concurrency could be tuned without a design change (see the `initial-implementation` change's design.md). Nothing in the CLI or profile configuration ever sets it, so both stages always fall back to `Environment.ProcessorCount` - a default tuned for NVMe queue depth. For Vara's most common workload, backing up from a fast internal drive to a slower external HDD target, that default is actively harmful: concurrent writers interleave their write streams against the mirror, forcing the HDD's head to jump between them instead of committing one file's write as a coherent stream. The source-side move-detection hashing pass in `BackupPlanner`, by contrast, never touches the target and benefits from staying highly concurrent. There is currently no way for a user to address this short of recompiling.

## What Changes

- Add an optional, per-profile concurrency configuration section with two independent settings: `scan_concurrency` (bounds `BackupPlanner`'s move-detection hashing concurrency) and `transfer_concurrency` (bounds `BackupExecutor`'s Add/Change transfer concurrency).
- When a setting is omitted, each stage keeps its own independent default rather than sharing one: `scan_concurrency` defaults to `Environment.ProcessorCount` (unchanged current behavior), while `transfer_concurrency` defaults to a new hardcoded value of `1`, matching the common NVMe-source/HDD-target scenario where serialized transfer keeps the target's write pattern coherent.
- Wire the resolved per-profile values from `BackupPipeline` into `BackupPlanner` and `BackupExecutor`'s existing `maxDegreeOfParallelism` parameters, which are otherwise unused today.
- Validate configured concurrency values as positive whole numbers, reporting a validation error identifying the profile, field name, and offending value for zero, negative, or non-numeric input - consistent with the existing retention-count validation pattern.

## Capabilities

### New Capabilities

(none)

### Modified Capabilities

- `profile-config`: profiles gain an optional concurrency configuration section (`scan_concurrency`, `transfer_concurrency`), with validation rules for invalid values.
- `backup-execution`: the system must honor a profile's configured scan/transfer concurrency limits when present, and the transfer stage's unconfigured default changes from processor-count to a fixed low value suited to the common target-disk scenario, independent of the scan stage's default.

## Impact

- `Vara.Core.Configuration.Profile` - new optional `ConcurrencySettings`-shaped record (mirroring the existing `RetentionPolicy` nullable-nested-record pattern).
- `Vara.Infrastructure.Configuration.YamlProfileConfigLoader` - parse and validate `scan_concurrency`/`transfer_concurrency` from the `concurrency` YAML mapping.
- `Vara.Application.Backup.BackupPipeline` - pass resolved concurrency values into `BackupPlanner`/`BackupExecutor` instead of using their parameterless defaults.
- `Vara.Application.Backup.BackupPlanner` / `BackupExecutor` - `transfer_concurrency`'s fallback default changes from `Environment.ProcessorCount` to a named constant of `1`; `BackupPlanner`'s fallback is unchanged.
- No CLI surface changes; this is profile-configuration-only.
