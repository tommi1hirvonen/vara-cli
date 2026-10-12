## Context

See `proposal.md` for motivation. The codebase is written in modern C# (.NET 11) using file-scoped namespaces, nullable reference types, and records. The root `Directory.Build.props` already configures `<TargetFramework>net11.0</TargetFramework>` and `<LangVersion>preview</LangVersion>` for all projects, ensuring C# 15 features (unions, closed hierarchies) are available immediately without any tooling changes.

However, several domain and presentation concepts rely on older patterns:
- In `Vara.Application.Backup`, `PlannedOperation` is a flat 11-field record whose valid fields depend on `PlannedOperationKind`. 7 fields are nullable, forcing defensive code and `!` assertions.
- In `Vara.Cli.Presentation`, `BackupProgressRole` and `BackupProgressOutcome` are open enums co-located in `BackupProgressColumn.cs`. Switch expressions over these enums rely on `_ =>` fallbacks, hiding unhandled states (such as `Running` being grouped into `_ =>` in `ApplyOutcomeStyle`).
- Multi-type files like `BackupProgressColumn.cs` (261 lines) and `BackupModels.cs` (131 lines) house both domain logic/renderers and various supporting enum/struct types in the same file.
- In `Vara.Core.Abstractions`, `ScannedEntry` is a heap-allocated class instantiated for every enumerated file during scans.
- In `Vara.Core.Snapshots`, 13 exception classes inherit directly from `System.Exception`, requiring an uncoordinated pattern list and a `default: return false;` fallback in `ErrorReporting.cs` because domain exceptions lack a unified closed root.
- In `Vara.Application.History`, domain-expected refusal conditions (binary files, symlinks, size limits) during diff and show are communicated by throwing exceptions.
- In `Vara.Cli.Commands.ProfilesCommand`, menu rows combine enums with nullable data fields (`MenuRowKind` + `Profile?`).

C# 15 introduces `union` declarations (and custom `[Union]` structs with `TryGetValue` non-boxing patterns) and the `closed` class modifier with same-assembly exhaustive pattern matching.

## Goals / Non-Goals

**Goals:**
- Guarantee compile-time exhaustiveness in `switch` expressions across planned operations, progress reporting roles/outcomes, exception handling, inspection results, and CLI prompts.
- Eliminate defensive discard arms (`_ =>`) in presentation styling, lifecycle dispatch, and error reporting.
- Eliminate nullable property ambiguity in `PlannedOperation` and `DirectoryEntry`.
- Prevent file bloat by isolating expanded closed hierarchies and unions into dedicated source files.
- Reduce per-item memory consumption in `BackupPlan`, avoid allocations during UI rendering with cached singletons, and eliminate heap allocations on the hot file-scanning loop.
- Replace exception-driven control flow for regular user-facing outcomes in diff/show operations with explicit union return types.
- Update unit, integration, and fake test harnesses cleanly using ergonomic test helper factories.
- Maintain 100% functional equivalence and test pass rate.

**Non-Goals:**
- Altering the SQLite manifest database schema or SQLite query layer.
- Changing CLI commands, options, exit codes, or user-visible terminal outputs.
- Modifying general-purpose I/O exception handling (e.g. `IOException`, `UnauthorizedAccessException`).
- Modifying `Directory.Build.props` (prerequisite is already configured).

## Decisions

### 1. Model `PlannedOperation` as a `closed record` Hierarchy in a Dedicated File
**Decision**: In a new file `src/Vara.Application/Backup/PlannedOperation.cs`, define:
```csharp
public closed record PlannedOperation(string RelativePath);

public sealed record AddOperation(
    string RelativePath,
    string SourceAbsolutePath,
    long Size,
    DateTimeOffset SourceModifiedAt) : PlannedOperation(RelativePath);

public sealed record ChangeOperation(
    string RelativePath,
    string SourceAbsolutePath,
    long Size,
    DateTimeOffset SourceModifiedAt,
    string? PreviousContentHash) : PlannedOperation(RelativePath);

public sealed record MoveOperation(
    string RelativePath,
    string PreviousRelativePath,
    string KnownContentHash,
    long Size,
    DateTimeOffset SourceModifiedAt,
    string? QuickHash = null,
    int? QuickHashScheme = null) : PlannedOperation(RelativePath);

public sealed record DeleteOperation(
    string RelativePath,
    long Size,
    DateTimeOffset SourceModifiedAt,
    string? KnownContentHash,
    string? QuickHash = null,
    int? QuickHashScheme = null) : PlannedOperation(RelativePath);

public sealed record LinkOperation(
    string RelativePath,
    string? PreviousContentHash,
    string? LinkTarget) : PlannedOperation(RelativePath);
```
**Rationale**:
- Eliminates 7 nullable fields and the associated documentation specifying which fields apply to which operations.
- `BackupExecutor` can match variants exhaustively with no fallback and no `!` operators.
- Reduces memory per `DeleteOperation` and `LinkOperation` from ~104 bytes to ~32–48 bytes per instance in `BackupPlan`.
- Keeping the hierarchy in a dedicated file prevents cluttering `BackupModels.cs`.

### 2. Model Presentation and Lifecycle Enums as Closed Singleton Hierarchies in Dedicated Files
**Decision**: Replace `BackupProgressRole` and `BackupProgressOutcome` in `BackupProgressColumn.cs` with closed record hierarchies in their own files:
- `src/Vara.Cli/Presentation/BackupProgressRole.cs`:
  ```csharp
  public closed record BackupProgressRole
  {
      public static readonly BackupProgressRole Scan = new ScanRole();
      public static readonly BackupProgressRole Bar = new BarRole();
      public static readonly BackupProgressRole Stats = new StatsRole();

      public sealed record ScanRole : BackupProgressRole;
      public sealed record BarRole : BackupProgressRole;
      public sealed record StatsRole : BackupProgressRole;
  }
  ```
- `src/Vara.Cli/Presentation/BackupProgressOutcome.cs`:
  ```csharp
  public closed record BackupProgressOutcome
  {
      public static readonly BackupProgressOutcome Running = new RunningOutcome();
      public static readonly BackupProgressOutcome Success = new SuccessOutcome();
      public static readonly BackupProgressOutcome PartialFailure = new PartialFailureOutcome();
      public static readonly BackupProgressOutcome Error = new ErrorOutcome();

      public sealed record RunningOutcome : BackupProgressOutcome;
      public sealed record SuccessOutcome : BackupProgressOutcome;
      public sealed record PartialFailureOutcome : BackupProgressOutcome;
      public sealed record ErrorOutcome : BackupProgressOutcome;
  }
  ```
- Move `BackupProgressState` to `src/Vara.Cli/Presentation/BackupProgressState.cs`.

**Rationale**:
- `BackupProgressColumn` rendering matches all roles and outcomes exhaustively without `_ =>` fallbacks:
  ```csharp
  var color = outcome switch
  {
      RunningOutcome => OutcomeStyle.Neutral.Foreground,
      SuccessOutcome => OutcomeStyle.Success.Foreground,
      PartialFailureOutcome => OutcomeStyle.PartialFailure.Foreground,
      ErrorOutcome => OutcomeStyle.Error.Foreground,
  };
  ```
- Static singletons ensure **zero heap allocations** per render call, maintaining peak UI loop performance.
- File decomposition keeps `BackupProgressColumn.cs` focused strictly on Spectre column layout and rendering.

### 3. Implement Zero-Allocation `ScanItem` Struct Union in a Dedicated File
**Decision**: In a new file `src/Vara.Core/Abstractions/ScanItem.cs`, define:
```csharp
public readonly record struct ScannedFile(
    string RelativePath,
    string AbsolutePath,
    long Size,
    DateTimeOffset ModifiedAt);

public readonly record struct ScannedLink(
    string RelativePath,
    string AbsolutePath,
    string LinkTarget);

[System.Runtime.CompilerServices.Union]
public readonly struct ScanItem : System.Runtime.CompilerServices.IUnion
{
    private readonly ScannedFile _file;
    private readonly ScannedLink _link;
    private readonly byte _tag; // 1 = File, 2 = Link

    public ScanItem(ScannedFile file) { _file = file; _link = default; _tag = 1; }
    public ScanItem(ScannedLink link) { _file = default; _link = link; _tag = 2; }

    public bool HasValue => _tag != 0;
    public bool TryGetValue(out ScannedFile file) { file = _file; return _tag == 1; }
    public bool TryGetValue(out ScannedLink link) { link = _link; return _tag == 2; }
    public object? Value => _tag switch { 1 => _file, 2 => _link, _ => null };

    public static ScanItem File(string rel, string abs, long size, DateTimeOffset mod) => new(new ScannedFile(rel, abs, size, mod));
    public static ScanItem Link(string rel, string abs, string target) => new(new ScannedLink(rel, abs, target));
}
```
`IFileSystemScanner.Scan` yields `IEnumerable<ScanItem>`.
**Rationale**:
- File scanning traverses 100,000+ files. Structs avoid GC allocations entirely.
- The C# 15 compiler lowers `item switch { ScannedFile f => ..., ScannedLink l => ... }` directly to calls to `TryGetValue`, avoiding boxing.
- Factory methods `ScanItem.File(...)` and `ScanItem.Link(...)` provide clean syntax for production code and tests alike.

### 4. Root `VaraDomainException` Closed Hierarchy for Exhaustive Error Handling
**Decision**: In a new file `src/Vara.Core/Exceptions/VaraDomainException.cs`, introduce:
```csharp
namespace Vara.Core.Exceptions;

/// <summary>
/// Closed root for all user-facing domain errors produced by Vara.
/// </summary>
public closed class VaraDomainException(string message, Exception? innerException = null)
    : Exception(message, innerException);
```
Subclass all domain exception families under `VaraDomainException`:
- `public closed class ProfileConfigException(...) : VaraDomainException(...)`
- `public closed class SnapshotException(...) : VaraDomainException(...)`
- `public closed class ConcurrencyException(...) : VaraDomainException(...)`
- `public closed class BackupRunException(...) : VaraDomainException(...)`
- `public sealed class InvalidDateTimeOptionException(...) : VaraDomainException(...)`

Because C# closed classes can only be subclassed in their defining assembly, define
`InvalidDateTimeOptionException` in `Vara.Core.Exceptions` and have the CLI parser
reference that Core type. Keeping its declaration in the CLI assembly would make it
impossible to derive from the closed Core root.

Refactor `ErrorReporting.Run` in `Vara.Cli/Composition/ErrorReporting.cs`:
```csharp
public static int Run(Action action, IAnsiConsole? errorConsole = null)
{
    try
    {
        action();
        return ExitCodes.Success;
    }
    catch (VaraDomainException ex)
    {
        OutcomeStyle.WriteLineError(errorConsole ?? StandardError.Console, $"Error: {ex.Message}");
        return ExitCodes.HardError;
    }
    catch (Exception ex)
    {
        var console = errorConsole ?? StandardError.Console;
        OutcomeStyle.WriteLineError(console, $"Unhandled exception: {ex.GetType().Name}: {ex.Message}");
        if (!string.IsNullOrEmpty(ex.StackTrace))
        {
            console.WriteLine(ex.StackTrace, new Style(Color.Grey));
        }
        return ExitCodes.HardError;
    }
}
```
**Rationale**:
- Eliminates `TryGetFriendlyMessage` and its defensive `default` case.
- Replaces runtime exception filters (`when (...)`) with standard, efficient CLR type dispatch.
- Guarantees compile-time exhaustiveness across all domain errors.

### 5. Replace Refusal Exceptions with Union Return Types for Diff & Show
**Decision**: In `src/Vara.Application/History/DiffResult.cs` and `ShowResult.cs`:
```csharp
public sealed record DiffStreams(Stream Left, Stream Right) : IDisposable
{
    public void Dispose() { Left.Dispose(); Right.Dispose(); }
}

public closed record DiffRefusal(string Message);
public sealed record OversizedDiffRefusal(string Message, long Limit) : DiffRefusal(Message);
public sealed record BinaryDiffRefusal(string Message) : DiffRefusal(Message);
public sealed record LinkedEntryDiffRefusal(string Message) : DiffRefusal(Message);

public union DiffResult(DiffStreams, DiffRefusal);
```
`SnapshotHistoryService.OpenVersionsForDiff` returns `DiffResult`.
`DiffCommand` handles the union directly without a try/catch block.
**Rationale**:
- Binary content, symlinks, and oversized files are normal user conditions when browsing backup history, not exceptional faults.
- Communicates the full set of possible outcomes explicitly in the method signature.

### 6. Replace Menu Row Enums with Closed Action Records in `ProfilesCommand`
**Decision**:
```csharp
private closed record MenuAction(string Label);
private sealed record EditProfile(Profile Profile) : MenuAction($"Edit {Profile.Name}");
private sealed record AddNewProfile() : MenuAction("Add new profile...");
private sealed record DeleteProfile(Profile Profile) : MenuAction($"Delete {Profile.Name}...");
private sealed record QuitAction() : MenuAction("Quit");
private sealed record CancelAction() : MenuAction("Cancel");
```
**Rationale**:
- Eliminates `Profile?` property that is null for 4 out of 5 menu items.
- Eliminates runtime checks in the main loop; the action carries its payload directly.

### 7. Test Suite Adaptation Strategy
Extensive unit, integration, and fake test suites exercise the refactored types and must be updated to align with the new models:
1. **`FakeFileSystemScanner` & Scanner Tests**:
   - `tests/Vara.Application.Tests/Backup/Fakes.cs` and `tests/Vara.IntegrationTests/Backup/Fakes.cs` implement `IFileSystemScanner`. Update them to accept `IReadOnlyList<ScanItem>` and use `ScanItem.File(...)` and `ScanItem.Link(...)` factory helpers.
   - Update `BackupPipelineTests`, `BackupPlannerTests`, `BackupPipelineRealRepositoryTests`, and `SymlinkLifecycleRealPortsTests` where `new ScannedEntry(...)` is instantiated to use `ScanItem` factories.
2. **`BackupExecutorTests` & `BackupPlannerTests`**:
   - Replace legacy flat `new PlannedOperation(PlannedOperationKind.Add, ...)` in `BackupExecutorTests` and `BackupExecutorCheckpointRealRepositoryTests` with direct typed construction: `new AddOperation(...)`, `new MoveOperation(...)`, `new DeleteOperation(...)`.
   - Update `BackupPlannerTests` assertions from `Assert.Equal(PlannedOperationKind.Add, op.Kind)` to `Assert.IsType<AddOperation>(op)`.
3. **`SnapshotHistoryServiceTests` & Inspection Tests**:
   - In `SnapshotHistoryServiceTests` and `SnapshotHistoryServiceRealPortsTests`, replace `Assert.Throws<DiffContentTooLargeException>(() => service.OpenVersionsForDiff(...))` with assertions against the returned `DiffResult` union (e.g. `var refusal = Assert.IsType<OversizedDiffRefusal>(result.Value);`).
   - Similarly update `ShowVersion` tests to assert on `ShowResult` union outcomes.
4. **`BackupProgressColumnTests` & Presentation Tests**:
   - Update `CreateTask` in `BackupProgressColumnTests` to accept closed `BackupProgressRole` and `BackupProgressOutcome` singletons, verifying exhaustiveness and styling without reliance on enum integer default values.

## Risks / Trade-offs

- **[Risk] Struct union size in `ScanItem`**: If `ScannedFile` has multiple fields, `ScanItem` size is the sum of its struct cases plus the tag.
  - *Mitigation*: Keep `ScannedFile` (4 fields = 40 bytes) and `ScannedLink` (3 fields = 24 bytes) compact. Total struct size is ~48 bytes, passing easily on the stack without copying bottlenecks.
- **[Risk] Test suite churn**: A high number of test files (~8 test suites) instantiate `PlannedOperation`, `ScannedEntry`, or assert on exceptions.
  - *Mitigation*: Provide ergonomic factory methods (`ScanItem.File(...)`, `ScanItem.Link(...)`) so test code is even clearer and more concise than the previous 6-to-11-parameter constructor calls.

## Migration Plan

1. Verify environment builds cleanly with existing `Directory.Build.props` configuration (`net11.0` / `LangVersion preview`).
2. Create `VaraDomainException.cs` and reparent all domain exceptions under it, refactoring `ErrorReporting.Run` to catch `VaraDomainException` directly.
3. Introduce `ScanItem` struct union in `Vara.Core.Abstractions/ScanItem.cs`; update scanner, differ, and `FakeFileSystemScanner` test fakes.
4. Refactor `PlannedOperation` to closed hierarchy in `Vara.Application.Backup/PlannedOperation.cs`; update planner, executor, and test suites (`BackupPlannerTests`, `BackupExecutorTests`).
5. Introduce `DiffResult` and `ShowResult` unions in `Vara.Application.History`; update `DiffCommand`, `ShowCommand`, and `SnapshotHistoryServiceTests`.
6. Extract `BackupProgressRole.cs`, `BackupProgressOutcome.cs`, and `BackupProgressState.cs`; refactor `BackupProgressColumn` and `BackupProgressColumnTests`.
7. Refactor `ProfilesCommand` interactive menus to closed action hierarchies.
8. Run all test suites across all projects (`dotnet test`) and verify 100% pass rate.
9. Run Native AOT build (`dotnet publish src/Vara.Cli -c Release`) to verify trimming and code generation.
