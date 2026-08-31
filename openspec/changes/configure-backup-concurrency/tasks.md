## 1. Domain model

- [x] 1.1 Add `ConcurrencySettings` record to `Vara.Core.Configuration` with nullable `ScanConcurrency`/`TransferConcurrency` int properties, validating each provided value is positive (`ArgumentOutOfRangeException` otherwise, mirroring `RetentionPolicy`); verify with a new `ConcurrencySettingsTests` covering valid construction and the negative/zero rejection cases.
- [x] 1.2 Add a nullable `Concurrency` property to `Profile`, threaded through its constructor alongside `Retention`; verify `tests/Vara.Core.Tests/Configuration/ProfileTests.cs` covers a profile with and without concurrency configured.

## 2. Configuration loading

- [x] 2.1 Parse an optional `concurrency` mapping in `YamlProfileConfigLoader`, reading `scan_concurrency`/`transfer_concurrency` following the same helper pattern as `GetOptionalRetentionCount`, adapted to require a strictly positive whole number; construct `ConcurrencySettings` from the parsed values.
- [x] 2.2 Raise a profile-facing validation error (identifying profile, field name, and offending value) for a `scan_concurrency`/`transfer_concurrency` value that is not a valid whole number, and for one that is zero or negative; verify with new cases in `tests/Vara.Infrastructure.Tests/Configuration/YamlProfileConfigLoaderTests.cs` mirroring the existing retention validation tests.
- [x] 2.3 Verify a profile YAML file omitting the `concurrency` section still loads successfully with `Profile.Concurrency` equal to `null`.

## 3. Pipeline wiring

- [x] 3.1 In `BackupExecutor`, replace the transfer-stage fallback expression `maxDegreeOfParallelism > 0 ? maxDegreeOfParallelism : Environment.ProcessorCount` with a private named constant default of `1` (e.g. `DefaultTransferConcurrency`); verify `tests/Vara.Application.Tests/Backup/BackupExecutorTests.cs` asserts the unconfigured default is `1` rather than `Environment.ProcessorCount`.
- [x] 3.2 Confirm `BackupPlanner`'s existing fallback to `Environment.ProcessorCount` is untouched; add/keep a `BackupPlannerTests` assertion documenting this default is unchanged.
- [x] 3.3 Update `BackupPipeline.Run` to construct `BackupPlanner`/`BackupExecutor` with `profile.Concurrency?.ScanConcurrency ?? 0` and `profile.Concurrency?.TransferConcurrency ?? 0` respectively, instead of the parameterless calls; verify with new cases in `tests/Vara.Application.Tests/Backup/BackupPipelineTests.cs` asserting a configured value is passed through to each component (e.g. via a fake/spy capturing the constructed concurrency) and that an unconfigured profile leaves each component's own default in effect.

## 4. End-to-end verification

- [x] 4.1 Add an integration-level test (`tests/Vara.IntegrationTests/Backup`) covering a profile with `transfer_concurrency` configured to a value greater than 1, asserting the backup run still produces a correct one-to-one mirror (functional correctness under a non-default concurrency, not a timing/performance assertion).
- [x] 4.2 Run the full test suite (`dotnet test`) and confirm all existing and new tests pass.

## 5. Documentation

- [x] 5.1 Add a `concurrency:` section to the `files` profile in `docs/config-sample.yml`, alongside the existing `retention:` section, showing both `scan_concurrency` and `transfer_concurrency`; verify by visually diffing against the existing `retention:` section's comment style (inline `#` comments explaining optionality, matching e.g. `recursive: false # default = true`).
- [x] 5.2 In that same sample section, comment that the section is entirely optional, and state each field's default explicitly (`scan_concurrency` defaults to the number of available processor cores; `transfer_concurrency` defaults to `1`) and briefly why the transfer default is low (backups commonly target slower external drives); verify by re-reading the comment against design.md's Decisions for accuracy.
