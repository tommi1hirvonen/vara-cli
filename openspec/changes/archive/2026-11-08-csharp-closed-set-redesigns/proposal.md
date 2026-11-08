## Why

A few closed-set shapes in the backup path and the profiles editor admit states their consumers cannot receive, and each such state is guarded by an "impossible case" `throw`:

- `PendingChange` pairs a file-or-link `ScanItem` with a kind that repeats that information;
- `PlannedOperation` is one flat hierarchy, although the executor's metadata pass and transfer path each handle only part of it;
- one `MenuAction` hierarchy serves two different prompts.

C# 15 `closed record` hierarchies and unions are checked for exhaustiveness across assemblies, so shaping each type to what its consumers can actually receive turns these run-time throws into compile-time guarantees. The typed `PendingChange` payload also replaces a 72-byte `ScanItem` copy plus a kind with the 40-byte payload alone, so the diff stage should allocate less.

This is the first of two refactoring changes to the backup path. The second, `csharp-async-backup-recording`, builds on the `PlannedOperation` sub-bases introduced here.

## What Changes

- `PendingChange` becomes a `closed record` with the leaves `AddedFile(ScannedFile)`, `ChangedFile(ScannedFile)` and `LinkedEntry(ScannedLink)`. `PendingChangeKind` is deleted, along with the planner's `GetScannedFile` helper and its three "impossible case" throws.
- `PlannedOperation` gets two `closed record` sub-bases: `TransferOperation` (`AddOperation`, `ChangeOperation`, which carries their shared source fields) and `MetadataOperation` (`MoveOperation`, `DeleteOperation`, `LinkOperation`). The executor's metadata pass and transfer path take their own sub-base, which deletes four throws and both `Where(operation switch { ... })` filters.
- `BackupDiffer` dispatches on `ScanItem` with an exhaustive switch expression instead of `HasValue`/`TryGetValue` probing, which deletes its two throws. `ScanItem` stays the existing non-boxing `[Union]` struct, so the scan allocates nothing extra per entry.
- `ProfilesCommand`'s single `MenuAction` hierarchy is split into `MainMenuAction` and `DeleteMenuAction`, which deletes both "unexpected menu action" throws.
- CS8509 (non-exhaustive switch expression) becomes a build error in `Directory.Build.props`, so every closed-set switch expression stays exhaustive without `default` arms.

No behavior, output, on-disk format or CLI surface changes.

## Capabilities

### New Capabilities

None.

### Modified Capabilities

None. This is a behavior-preserving refactor (`skip_specs: true`): the specs describe what Vara does, not how its types are shaped.

## Impact

- **Code:** `src/Vara.Application/Backup/{BackupModels,PlannedOperation,BackupDiffer,BackupPlanner,BackupExecutor,BackupPipeline}.cs`, `src/Vara.Cli/Commands/ProfilesCommand.cs`, `Directory.Build.props`.
- **Tests:** `BackupDifferTests`, `BackupPlannerTests`, `BackupExecutorTests`, `BackupPipelineTests` and `BackupStageBenchmarks` construct or inspect `PendingChange`. They switch to the new leaves, and each test keeps its name and intent. `ScanItemTests` (including the zero-allocation test) and the scanner tests are unchanged.
- **Benchmarks:** `BackupDifferBenchmarks`, `BackupPlannerBenchmarks` and `DirectoryScanBenchmarks` are compared against the base-commit baseline `benchmarks/baselines/2026-11-06-csharp-net11`, using `benchmarks/Compare-BenchmarkResults.ps1`. No regression is expected, and the differ should allocate less.
- **Dependencies:** none.
