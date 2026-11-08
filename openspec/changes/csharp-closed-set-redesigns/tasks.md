## 1. Preparation

- [ ] 1.1 Confirm that the benchmark regression tooling is present: `benchmarks/Compare-BenchmarkResults.ps1`, `benchmarks/baselines/README.md`, and the baseline `benchmarks/baselines/2026-11-06-csharp-net11`, which was measured on this change's base commit (`3692197`). Verify that comparing the baseline against itself with the script reports every case as OK (52 cases).
- [ ] 1.2 Add `<WarningsAsErrors>$(WarningsAsErrors);CS8509</WarningsAsErrors>` to `Directory.Build.props` (design Decision 2). Verify that `dotnet build Vara.slnx` still succeeds, and that a temporary switch expression with a missing arm fails the build with CS8509. Remove the temporary switch afterwards.

## 2. Typed pending changes

- [ ] 2.1 Replace `PendingChange(ScanItem Entry, PendingChangeKind Kind)` and `PendingChangeKind` in `BackupModels.cs` with the `closed record PendingChange` and its `AddedFile`/`ChangedFile`/`LinkedEntry` leaves (design Decision 3). Verify with `git grep PendingChangeKind -- src tests benchmarks`, which must return nothing once 2.2 to 2.4 are done.
- [ ] 2.2 Rewrite `BackupDiffer.Diff` to dispatch on each `ScanItem` with a switch expression whose arms call static classifiers returning `PendingChange?`. Delete the `HasValue` check and both throws. Verify that `BackupDifferTests` pass.
- [ ] 2.3 Rewrite `BackupPlanner.Plan` to map each pending change with a switch expression through per-case helpers. Rewrite the move-candidate selection with `OfType<AddedFile>()`. Delete `GetScannedFile` and every throw tied to `PendingChangeKind`. Verify that `BackupPlannerTests` pass, including `A_tie_between_identical_candidates_is_broken_by_deleted_path_order_under_parallel_hashing`.
- [ ] 2.4 Update every test and benchmark that constructs or inspects `PendingChange` (`BackupDifferTests`, `BackupPlannerTests`, `BackupExecutorTests`, `BackupPipelineTests`, `BackupStageBenchmarks`) to use the new leaves, keeping test names and intent. Verify that `dotnet test Vara.slnx` passes and that `dotnet build benchmarks/Vara.Benchmarks -c Release` succeeds.

## 3. Operation sub-bases

- [ ] 3.1 Introduce `closed record TransferOperation` (with the shared `SourceAbsolutePath`, `Size` and `SourceModifiedAt`) and `closed record MetadataOperation` in `PlannedOperation.cs`, and re-parent the five leaves without changing their constructor signatures (design Decision 4). Verify that `dotnet build Vara.slnx` succeeds with no test edits needed for construction.
- [ ] 3.2 In `BackupExecutor`, drive the metadata pass with `OfType<MetadataOperation>()` and the transfer list with `OfType<TransferOperation>()`. Narrow `ExecuteMetadataOnlyOperation` to `MetadataOperation` and `ExecuteTransfer` to `TransferOperation`, read the shared fields from the base, and delete both `Where(operation switch ...)` filters and all four throws. Verify that `BackupExecutorTests` and the IntegrationTests pass.
- [ ] 3.3 Change `BackupPlan.TotalFilesToTransfer` to count `TransferOperation`s, and rewrite `BackupPipeline.PlanOnly`'s counting switch as an exhaustive switch expression. Verify that `BackupPipelineTests` (the dry-run counts) pass.

## 4. Profiles menus

- [ ] 4.1 Split `MenuAction` into `MainMenuAction` and `DeleteMenuAction`, give each prompt its own type, delete both "unexpected menu action" throws, and update the converter comment (design Decision 5). Verify that the `ProfilesCommand` tests in `Vara.Cli.Tests` pass, and that `git grep "Unexpected .*menu action" -- src` returns nothing.

## 5. Verification

- [ ] 5.1 Verify that no impossible-case throw remains in the backup path: `git grep -n "InvalidOperationException\|ArgumentOutOfRangeException" -- src/Vara.Application/Backup` lists only real run-time conditions (for example "A move match must have a content hash"), and the commit message lists each remaining one with its reason.
- [ ] 5.2 Publish `src/Vara.Cli` with Native AOT (`dotnet publish src/Vara.Cli -c Release -r win-x64`). Verify that there are no IL2xxx/IL3xxx warnings and that `vara --help` runs.
- [ ] 5.3 Run `dotnet run -c Release --project benchmarks/Vara.Benchmarks -- --filter '*BackupDifferBenchmarks*' '*BackupPlannerBenchmarks*' '*DirectoryScanBenchmarks*' --job short --exporters fulljson --artifacts <temp folder>` and compare it with the base-commit baseline from task 1.1. Verify that no allocation regresses beyond tolerance, and record the differ's allocation change in the commit message. A flagged time case is re-run with the default job on both commits before it counts as a regression.
- [ ] 5.4 Verify that the full suite `dotnet test Vara.slnx` passes and that `dotnet build Vara.slnx` reports zero `CS` compiler warnings (the existing NU1903 advisory for the ILCompiler preview package is out of scope).
