## 1. Plan-only pipeline entry point

- [x] 1.1 Extract `BackupPipeline`'s scan→diff→plan sequence (`GetCurrentState`, `scanner.Scan`, `BackupDiffer.Diff`, `BackupPlanner.Plan`) into a small private helper shared by `Run` and the new method, so both stay in sync
- [x] 1.2 Add `BackupPipeline.PlanOnly(Profile profile)`: acquires the run lock (throwing `BackupAlreadyRunningException` on failure, same as `Run`), calls the shared scan→diff→plan helper, and returns a new `BackupPlanSummary` record (counts per `PlannedOperationKind`, `TotalBytesToTransfer`, scan failure paths) - without calling `ReconcileIncompleteSnapshots`, `ProbeHardlinkSupport`, `CleanupOrphanedTemp`, `BeginSnapshot`, or `BeginManifestBatch`
- [x] 1.3 Add `Vara.Application.Tests` asserting `PlanOnly` performs zero writes: the mirror, content store, and manifest (snapshot list) are unchanged after a call, for a profile with pending adds/changes/deletes/moves
- [x] 1.4 Add a test asserting `PlanOnly` throws `BackupAlreadyRunningException` when the run lock is already held, matching `Run`'s behavior
- [x] 1.5 Add a test asserting `PlanOnly`'s reported counts match what a real `Run` against the same profile state would report as added/changed/moved/deleted

## 2. Shared JSON result shape

- [x] 2.1 Define a JSON-serializable DTO (e.g. `BackupRunSummaryJson`) covering `mode`, `profile`, `startedAt`, `completedAt`, `cancelled`, `added`, `changed`, `moved`, `deleted`, `failed`, `failedPaths`, `bytesTransferred`, per design.md's shared schema
- [x] 2.2 Add a mapping function from `BackupRunResult` to the DTO with `mode: "executed"`
- [x] 2.3 Add a mapping function from `BackupPlanSummary` to the DTO with `mode: "dry-run"`, `cancelled: false`, and `failed`/`failedPaths` sourced from scan failures only
- [x] 2.4 Add serialization tests asserting the DTO's JSON shape for both a dry-run and an executed summary, including a case with non-empty `failedPaths`

## 3. CLI wiring

- [x] 3.1 Add `--dry-run` and `--json` options to `BackupCommand`
- [x] 3.2 When `--dry-run` is given, call `pipeline.PlanOnly(profile)` instead of `pipeline.Run(...)`, skipping the executor entirely
- [x] 3.3 When `--json` is given (with or without `--dry-run`), skip both `RunWithLiveDisplay` and `RunWithPlainOutput`'s progress rendering, run the pipeline call directly (no `IProgress<T>` callback needed), then serialize the mapped DTO and write it as the only content on stdout
- [x] 3.4 When `--json` is not given, add a plain-text dry-run summary formatter (reusing `BackupRunSummaryFormatter`'s style conventions) reporting planned counts/bytes, printed the same way `BackupOutcomeReporter` prints a real run's summary today
- [x] 3.5 Confirm exit-code logic: a dry run with scan failures maps to `ExitCodes.PartialFailure`, matching a real run's existing failed-paths behavior; zero failures maps to `ExitCodes.Success`
- [x] 3.6 Add `Vara.Cli.Tests` covering: `--dry-run` alone (plain-text planned summary, zero writes), `--json` alone (single JSON object on stdout for an executed run), `--dry-run --json` together (single JSON object with `mode: "dry-run"`), and that `--json`'s stdout contains no progress output in either mode

## 4. Documentation and verification

- [x] 4.1 Add `--dry-run` and `--json` to the `vara backup` entry in `README.md`'s command reference table
- [x] 4.2 Run `cd src; dotnet test ..\tests\Vara.Application.Tests` and verify `BackupPipelineTests` pass
- [x] 4.3 Run `cd src; dotnet test ..\tests\Vara.Cli.Tests` and verify the new `BackupCommand` tests pass
- [x] 4.4 Confirm each scenario in both delta specs (`backup-execution`, `cli-presentation`) is covered by a test from Tasks 1-3
