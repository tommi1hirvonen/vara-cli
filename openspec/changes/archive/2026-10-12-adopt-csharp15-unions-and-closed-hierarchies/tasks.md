## 1. Domain Exception Closed Hierarchies (`VaraDomainException`)

- [x] 1.1 Create `src/Vara.Core/Exceptions/VaraDomainException.cs` defining `public closed class VaraDomainException` as the closed root for all domain exceptions.
- [x] 1.2 Reparent `ProfileConfigException` under `VaraDomainException` in `Vara.Core/Configuration/ProfileConfigExceptions.cs` and verify compilation.
- [x] 1.3 Create `public closed class SnapshotException` in `Vara.Core/Snapshots/SnapshotExceptions.cs` deriving from `VaraDomainException` and reparent the 13 snapshot exception classes under it.
- [x] 1.4 Introduce closed bases `ConcurrencyException` and `BackupRunException` deriving from `VaraDomainException`.
- [x] 1.5 Define `InvalidDateTimeOptionException` in `Vara.Core.Exceptions` under `VaraDomainException`; have the CLI parser use the Core type so the root remains closed within its assembly.
- [x] 1.6 Refactor `ErrorReporting.Run` in `Vara.Cli/Composition/ErrorReporting.cs` to catch `VaraDomainException` directly, removing `TryGetFriendlyMessage` and eliminating the `default` fallback arm.
- [x] 1.7 Update and run `ErrorReportingTests.cs` to verify `VaraDomainException` handling and unhandled CLR exception fallthrough.

## 2. Hot-Path File Scanning Struct Union (`ScanItem`) & Test Fakes

- [x] 2.1 Define `readonly record struct ScannedFile` and `readonly record struct ScannedLink` with static factory helpers (`ScanItem.File(...)`, `ScanItem.Link(...)`) in `Vara.Core/Abstractions/ScanItem.cs`.
- [x] 2.2 Implement `[Union] readonly struct ScanItem : IUnion` with non-boxing `TryGetValue` accessors and `HasValue` in `Vara.Core/Abstractions/ScanItem.cs`; verify unit test confirms no boxing.
- [x] 2.3 Update `DirectoryFileSystemScanner` to yield `ScanItem` and update `BackupDiffer.Diff` to consume `ScanItem` via pattern matching.
- [x] 2.4 Update `FakeFileSystemScanner` in `tests/Vara.Application.Tests/Backup/Fakes.cs`, `tests/Vara.Cli.Tests/Commands/BackupCommandTests.cs`, and `tests/Vara.IntegrationTests/Backup/Fakes.cs` to accept `IReadOnlyList<ScanItem>`.
- [x] 2.5 Update scan-entry instantiation across `BackupPipelineTests`, `BackupPlannerTests`, `BackupPipelineRealRepositoryTests`, and `SymlinkLifecycleRealPortsTests` to use `ScanItem` factories; verify scanner and differ tests pass.

## 3. PlannedOperation Closed Record Hierarchy & Test Migration

- [x] 3.1 Define `closed record PlannedOperation` and sealed variants (`AddOperation`, `ChangeOperation`, `MoveOperation`, `DeleteOperation`, `LinkOperation`) in a dedicated file `Vara.Application/Backup/PlannedOperation.cs`.
- [x] 3.2 Remove legacy flat `PlannedOperation` and `PlannedOperationKind` from `Vara.Application/Backup/BackupModels.cs`.
- [x] 3.3 Refactor `BackupPlanner.Plan` to instantiate concrete operation types without nullable field compromise.
- [x] 3.4 Refactor `BackupExecutor.ExecuteAsync` and `BackupPlanSummary` to pattern match exhaustively over the closed operation hierarchy.
- [x] 3.5 Preserve `BackupPlanOutcomeReporter`'s aggregate-only output; exhaustively classify operation variants in `BackupPipeline.PlanOnly` before constructing the summary.
- [x] 3.6 Refactor test suites: update `BackupExecutorTests.cs` and `BackupExecutorCheckpointRealRepositoryTests.cs` to construct concrete operation classes (`AddOperation`, etc.), and update `BackupPlannerTests.cs` to assert with `Assert.IsType<AddOperation>(op)`; verify all backup tests pass.

## 4. Inspection Result Unions for Diff and Show & Test Assertion Updates

- [x] 4.1 Define `DiffStreams`, `DiffRefusal` (with closed sub-records for oversized, binary, and linked entries), and `union DiffResult(DiffStreams, DiffRefusal)` in a dedicated file `Vara.Application/History/DiffResult.cs`.
- [x] 4.2 Refactor `SnapshotHistoryService.OpenVersionsForDiff` to return `DiffResult` instead of throwing refusal exceptions.
- [x] 4.3 Define `ShowRefusal` and `union ShowResult(Stream, ShowRefusal)` in a dedicated file `Vara.Application/History/ShowResult.cs`, updating `SnapshotHistoryService.ShowVersion` to return `ShowResult`.
- [x] 4.4 Update `DiffCommand` and `ShowCommand` in `Vara.Cli/Commands` to pattern match over the union results and report refusals cleanly.
- [x] 4.5 Refactor `SnapshotHistoryServiceTests.cs` and `SnapshotHistoryServiceRealPortsTests.cs` from `Assert.Throws<Diff*Exception>` to assert on the returned `DiffRefusal` and `ShowRefusal` union cases; verify all diff and show tests pass.

## 5. Presentation Enums to Closed Singleton Hierarchies & Test Updates

- [x] 5.1 Create `src/Vara.Cli/Presentation/BackupProgressRole.cs` defining `closed record BackupProgressRole` with static singletons (`Scan`, `Bar`, `Stats`) and sealed role subclasses.
- [x] 5.2 Create `src/Vara.Cli/Presentation/BackupProgressOutcome.cs` defining `closed record BackupProgressOutcome` with static singletons (`Running`, `Success`, `PartialFailure`, `Error`) and sealed outcome subclasses.
- [x] 5.3 Move `BackupProgressState` out of `BackupProgressColumn.cs` into a dedicated file `src/Vara.Cli/Presentation/BackupProgressState.cs`.
- [x] 5.4 Refactor `BackupProgressColumn.Render` and `ApplyOutcomeStyle` to switch exhaustively over the closed `BackupProgressRole` and `BackupProgressOutcome` hierarchies without fallback discard arms (`_ =>`).
- [x] 5.5 Refactor `FileChangeKind` and `DirectoryEntryStatus` to closed hierarchies with exhaustive matching in `FileChangeKindStyle.cs` and `EntryStatusStyle.cs`.
- [x] 5.6 Update `BackupProgressColumnTests.cs`, `BackupCommandTests.cs`, `DirectoryListingPresenterTests.cs`, and `DeletedReportPresenterTests.cs` to use the closed singleton hierarchies and assert without relying on integer enum defaults; verify presentation tests pass.

## 6. CLI Interactive Wizard Closed Actions

- [x] 6.1 Define `closed record MenuAction` and sealed variants in `Vara.Cli/Commands/ProfilesCommand.cs`, eliminating `MenuRowKind` and `Profile?` nullable coupling.
- [x] 6.2 Define `closed record EditFieldAction` and `SourceFieldAction` hierarchies in `Vara.Cli/Commands/ProfilesCommand.cs`, replacing action enums with typed action handlers.
- [x] 6.3 Update the selection prompt conversions and execution loop in `ProfilesCommand` to match actions exhaustively; verify `ProfilesCommandTests.cs` and `ProfilesEndToEndTests.cs` pass.

## 7. Verification and Performance Validation

- [x] 7.1 Run full test suite across all test projects (`dotnet test`) and verify 100% pass rate.
- [x] 7.2 Run Native AOT build (`dotnet publish src/Vara.Cli -c Release`) to verify full compatibility with Native AOT trimming and compilation.
