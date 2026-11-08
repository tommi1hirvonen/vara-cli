## Context

See proposal.md - Why.

Current C# shapes on `main` (commit `3692197`):

- `PendingChange(ScanItem Entry, PendingChangeKind Kind)` duplicates the kind. `ScanItem` already says file or link, and `Kind` repeats it, so `BackupPlanner` probes with `TryGetValue`, and `GetScannedFile` plus three arms throw for combinations that cannot occur (`Linked` without a link, `Added`/`Changed` without a file, the enum's unnamed values).
- `PlannedOperation` is a flat `closed record` with five leaves. The executor filters `plan.Operations` twice with `Where(operation switch { ... => true/false })`. Its metadata pass and `ExecuteTransfer` then each switch over all five leaves, with four throw arms for the leaves the other path handles.
- `BackupDiffer` checks `HasValue` and then probes `TryGetValue(out ScannedLink)`/`TryGetValue(out ScannedFile)`, with two throws for the "neither" state.
- `ProfilesCommand` uses one `MenuAction` hierarchy for two prompts, so each prompt's switch has a throw arm for the other prompt's actions.

Facts verified with a throwaway two-assembly probe while planning (.NET SDK 11.0.100-rc.1, `LangVersion` preview):

- A `closed record` may have `closed record` sub-bases. A switch expression over a sub-base is exhaustive with that sub-base's leaves alone. A switch expression over the root is exhaustive with only the leaves listed: no sub-base arms are needed. Both hold from another assembly.
- A switch expression over the hand-written `[Union]` `ScanItem` with `ScannedFile f` and `ScannedLink l` arms is exhaustive from another assembly. It does not box: about 1 KB was allocated in total for 1000 items, essentially nothing per item. On `default(ScanItem)` it throws `SwitchExpressionException`.
- C# never checks a statement `switch` for exhaustiveness. A switch expression that misses a case is warning CS8509. `main` currently builds with zero compiler warnings.

## Goals / Non-Goals

**Goals:**
- No "impossible case" throw remains in the backup path or in `ProfilesCommand`'s menus.
- A missing case in a switch expression over a closed set is a build error.
- Allocation and time stay at or below the baseline, and the differ should allocate less.

**Non-Goals:**
- **`ScanItem` stays a struct.** A `closed record` hierarchy (`ScannedFile`/`ScannedLink` as leaves) was considered and rejected. It would add one allocation per scanned entry on the hottest path, and the union struct is already matched exhaustively from other assemblies without boxing. Its `HasValue`/`TryGetValue`/`Value` members are the union pattern's non-boxing hooks and stay.
- **`FileChangeKind` stays a `closed record`.** A plain enum was considered and rejected: an enum switch without `default` is never exhaustive (CS8509/CS8524, because an enum can hold unnamed values), while the `closed record` is.
- **Complete statement switches with side-effecting arms**, such as `SnapshotChangeReportBuilder.Count`, `DirectoryRestoreService`'s per-row counters and the profile field menus, already cover every case without a `default` or throw arm. Turning them into expressions would contort the code.
- **Concurrency, `TransferResult`, async and the render loop:** all in `csharp-async-backup-recording`.

## Decisions

### 1. Dispatch rule for closed sets

- When each arm produces a value, use a **switch expression**. The CS8509 error guarantees exhaustiveness.
- When the arms only perform side effects, a **complete statement switch** is acceptable.
- Never add a `default` or `_` arm, or a throw arm for a case that "cannot happen", to a switch over a `closed record` or union. If a consumer can only receive part of a hierarchy, the type should say so (a sub-base or a narrower hierarchy), as Decisions 3 to 5 do.
- `default` arms over open inputs (strings, YAML nodes, `SnapshotStatus` presentation, DiffPlex's `ChangeType`) are correct and stay.

*Alternative:* an analyzer for statement-switch exhaustiveness. Rejected: there is no built-in one, and a third-party analyzer is a new dependency for a handful of sites.

### 2. CS8509 is an error

`Directory.Build.props` gets `<WarningsAsErrors>$(WarningsAsErrors);CS8509</WarningsAsErrors>`. It applies to `src`, `tests` and `benchmarks`. CS8524 (unnamed enum values) is not included, because it would force `default` arms onto enum switches, which is the opposite of the goal.

### 3. `PendingChange` carries a typed payload

```csharp
public closed record PendingChange;
public sealed record AddedFile(ScannedFile File) : PendingChange;
public sealed record ChangedFile(ScannedFile File) : PendingChange;
public sealed record LinkedEntry(ScannedLink Link) : PendingChange;
```

- `PendingChangeKind` is deleted. `DiffResult.Pending` stays `IReadOnlyList<PendingChange>`.
- `BackupDiffer` switches over `ScanItem` with a switch expression. Each arm calls a small static classifier (`ClassifyFile`, `ClassifyLink`) that returns `PendingChange?`, where `null` means unchanged or already linked. Both throws and the `HasValue` check are deleted. `default(ScanItem)` now surfaces as `SwitchExpressionException`, which is no less safe than today's `InvalidOperationException` for a state the scanner never produces.
- `BackupPlanner` maps each change with a switch expression to its `PlannedOperation`, through per-case helpers (`PlanLink`, `PlanAddedOrMove`, `PlanChanged`). `GetScannedFile`, the `Linked` throws and the `ArgumentOutOfRangeException` arm are deleted. The move-candidate pre-filter uses `diff.Pending.OfType<AddedFile>()`.
- Each leaf stores the 40-byte `ScannedFile`/`ScannedLink` inline instead of the 72-byte `ScanItem` plus the kind, so each pending change should allocate about 40 bytes less. The benchmark comparison (Decision 6) measures the actual saving.

*Alternatives:*
- `PendingChange(ScanItem Entry)` plus a kind derived on demand. Rejected: an `Added`/`Changed` distinction still needs a second field, so a file-or-link question would remain.
- A union `PendingChange(AddedFile, ChangedFile, LinkedEntry)`. Rejected: the leaves are already references, so a union adds nothing over a `closed record` here.

### 4. `PlannedOperation` gets transfer and metadata sub-bases

```csharp
public closed record PlannedOperation(string RelativePath);
public closed record TransferOperation(string RelativePath, string SourceAbsolutePath, long Size, DateTimeOffset SourceModifiedAt)
    : PlannedOperation(RelativePath);
public closed record MetadataOperation(string RelativePath) : PlannedOperation(RelativePath);
// AddOperation, ChangeOperation : TransferOperation;  MoveOperation, DeleteOperation, LinkOperation : MetadataOperation
```

- `TransferOperation` carries the four fields `AddOperation` and `ChangeOperation` share. `ChangeOperation` adds `PreviousContentHash`. Constructor signatures and positional order of the leaves are unchanged, so call sites and test construction stay the same.
- `ExecuteTransfer` takes a `TransferOperation`. It reads the shared fields directly and derives `(previousContentHash, changeKind)` with a two-arm switch expression. The three throws are deleted.
- The metadata pass iterates `plan.Operations.OfType<MetadataOperation>()` and takes a `MetadataOperation`. Its switch covers three leaves, and the `AddOperation`/`ChangeOperation` throw is deleted. The transfer list is `plan.Operations.OfType<TransferOperation>().ToList()`. Both `Where(operation switch ...)` filters are deleted.
- `BackupPlan.Operations` keeps the planned order. `TotalFilesToTransfer` becomes `Operations.Count(o => o is TransferOperation)`.
- `BackupPipeline.PlanOnly`'s counting switch becomes a switch expression over the root, mapping each leaf to its counter, so a future leaf is a build error.

*Alternative:* two lists on `BackupPlan`. Rejected: callers and tests rely on one ordered list, and `OfType` keeps the order within each partition.

### 5. `ProfilesCommand` gets one action hierarchy per menu

- `MainMenuAction(string Label)`: `EditProfile`, `AddNewProfile`, `StartDeleteProfile`, `QuitAction`.
- `DeleteMenuAction(string Label)`: `DeleteProfile`, `CancelAction`.

Each prompt is a `SelectionPrompt` of its own type. The main loop's statement switch covers its four leaves, the delete flow's switch expression covers two, and both throws are deleted. The `UseConverter` comment that names `MenuAction` is updated. The edit-field and source hierarchies are already separate and stay.

### 6. Verification

- `dotnet build Vara.slnx` and `dotnet test Vara.slnx` after each task group.
- A Native AOT publish of `src/Vara.Cli` once at the end, with no new IL2xxx/IL3xxx warnings.
- Benchmarks: `BackupDifferBenchmarks`, `BackupPlannerBenchmarks` and `DirectoryScanBenchmarks`, run with `--job short --exporters fulljson`, compared against the base-commit baseline (task 1.1) with `Compare-BenchmarkResults.ps1`. Allocations are the reliable signal; time is advisory at `--job short`, because three iterations catch large regressions but not small ones.

## Risks / Trade-offs

- [`WarningsAsErrors` for CS8509 breaks the build on an existing switch] → `main` builds with zero warnings today. If a test or benchmark switch trips it, fix the switch rather than suppress the warning.
- [Record equality or `ToString` changes for `PendingChange`] → Tests compare kinds and paths, not whole records. Any test that compares a whole `PendingChange` is updated to compare the leaf type and payload.
- [The move-detection tie-break order changes] → The planner still iterates `diff.Pending` in order and consumes candidates serially. `A_tie_between_identical_candidates_is_broken_by_deleted_path_order_under_parallel_hashing` guards this.
- [Benchmark noise at `--job short`] → Judge by allocations. Re-run a flagged time case with the default job before drawing conclusions.

## Migration Plan

One commit per task group, each green on `dotnet test Vara.slnx`. On-disk formats and CLI behavior do not change. Rollback is a revert.
