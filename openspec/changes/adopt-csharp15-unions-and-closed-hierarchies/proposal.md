## Why

The `vara-cli` codebase currently models several multi-variant domain concepts using either enums paired with flat records containing numerous nullable fields (such as `PlannedOperation`), loose exception hierarchies with catch-all pattern matching, or exception-driven control flow for expected operation refusals (such as binary/oversized diff rejections).

Furthermore, key presentation and lifecycle concepts rely on standard C# enums (such as `BackupProgressRole`, `BackupProgressOutcome`, `FileChangeKind`, and `DirectoryEntryStatus`). Because enums are open-ended integral types in C#, every switch expression must include a defensive fallback arm (`_ =>`), which conceals unhandled states. For example, in `BackupProgressColumn`, the `Running` outcome is not even explicitly matched; it silently falls into `_ =>` alongside any future outcome variants, inheriting partial-failure styling without any compiler diagnostic.

Similarly, CLI error reporting currently intercepts exceptions using `catch (Exception ex) when (TryGetFriendlyMessage(ex, out var message))`, requiring a defensive `default: return false;` fallback over `System.Exception` because domain exceptions lack a unified closed root.

With C# 15's introduction of **union types** (`union`) and **closed class hierarchies** (`closed class`), the language provides first-class support for compile-time exhaustive pattern matching and discriminated unions. Adopting these features improves domain modeling rigor, eliminates impossible states, removes defensive null-forgiving operators (`!`), guarantees compile-time exhaustiveness across UI and lifecycle states, and reduces heap allocation overhead on both hot scanning paths and warm execution batches.

The project repository already targets `net11.0` with `<LangVersion>preview</LangVersion>` enabled centrally in `Directory.Build.props`, so all C# 15 language capabilities are immediately available without project configuration changes.

## What Changes

1. **Closed Record Hierarchy for Backup Operations (`PlannedOperation`)**:
   - Replace the flat `PlannedOperation` record (which had 11 fields, 7 of which were conditionally nullable depending on `PlannedOperationKind`) with a `closed record PlannedOperation(string RelativePath)`.
   - Introduce dedicated sealed variants: `AddOperation`, `ChangeOperation`, `MoveOperation`, `DeleteOperation`, and `LinkOperation`.
   - Update `BackupPlanner` to produce typed operation variants and `BackupExecutor` / reporters to switch exhaustively over the closed hierarchy.

2. **Presentation & Lifecycle Enums to Closed Singleton Hierarchies**:
   - Replace open enums (`BackupProgressRole`, `BackupProgressOutcome`, `FileChangeKind`, `DirectoryEntryStatus`) with closed record hierarchies using cached static singletons.
   - Eliminate defensive `_ =>` discard arms in rendering methods such as `BackupProgressColumn.Render` and `ApplyOutcomeStyle`, guaranteeing compiler errors if new states are added without matching arms.

3. **Dedicated File Decomposition for Expanded Types**:
   - Decompose multi-type files to prevent bloating:
     - Move `BackupProgressRole`, `BackupProgressOutcome`, and `BackupProgressState` out of `BackupProgressColumn.cs` into dedicated files in `Vara.Cli/Presentation`.
     - Move `PlannedOperation` and its variants out of `BackupModels.cs` into `PlannedOperation.cs` in `Vara.Application/Backup`.
     - Move `ScanItem` and its struct cases into `ScanItem.cs` in `Vara.Core/Abstractions`.
     - Define `DiffResult` and `ShowResult` in dedicated files in `Vara.Application/History`.

4. **Zero-Allocation Struct Union for Source Traversal (`ScanItem`)**:
   - Model scanned items with value types (`readonly record struct ScannedFile`, `readonly record struct ScannedLink`) wrapped in a custom struct union `ScanItem` implementing C# 15's non-boxing access pattern (`TryGetValue`, `HasValue`).
   - Streamline `DirectoryFileSystemScanner` and `BackupDiffer` to process scanned items without per-file heap allocations or boxing on hot scanning loops.

5. **Closed Hierarchy for Domain Exceptions with Root `VaraDomainException`**:
   - Introduce a root `public closed class VaraDomainException : Exception`.
   - Subclass domain exception families under `VaraDomainException` (`ProfileConfigException`, `SnapshotException`, `ConcurrencyException`, `BackupRunException`, `InvalidDateTimeOptionException`).
   - Refactor `ErrorReporting.Run` to catch `VaraDomainException` directly, enabling 100% exhaustive pattern matching with no `default` case and eliminating filter methods.

6. **Result-or-Refusal Union Types for Diff and Show Operations**:
   - Replace exception-driven control flow for domain refusals (oversized content, binary content, symlinks without content) in `SnapshotHistoryService`.
   - Introduce `public union DiffResult(DiffStreams, DiffRefusal)` and `public union ShowResult(Stream, ShowRefusal)`.
   - Update `DiffCommand` and `ShowCommand` to exhaustively handle success and refusal cases without catching exceptions for regular user-facing outcomes.

7. **Closed Action Hierarchies for Interactive CLI Wizards**:
   - Replace loose enum-plus-nullable data rows in `ProfilesCommand` (`MenuRow`, `EditActionRow`, `SourceFieldActionRow`) with closed record hierarchies (`MenuAction`, `EditFieldAction`, `SourceFieldAction`).
   - Enable direct, type-safe pattern matching on selected actions without manual casting or null assertions.

8. **Test Suite Modernization and Adaptation**:
   - Update unit, integration, and fake test suites to use the refactored types and ergonomic test factories (`ScanItem.File(...)`, `ScanItem.Link(...)`).
   - Replace exception-throwing assertions in diff/show tests with assertions against union result variants.
   - Replace enum checks in planner/executor tests with `Assert.IsType<T>` checks on concrete operation subclasses.

## Capabilities

### New Capabilities
<!-- None: Pure architectural and domain modeling refactoring -->

### Modified Capabilities
<!-- None: CLI commands, flags, backup format, and externally observable behaviors are strictly preserved. skip_specs: true is enabled. -->

## Impact

- **Core & Application Models**: `Vara.Core.Abstractions`, `Vara.Core.Configuration`, `Vara.Core.Snapshots`, `Vara.Core.Exceptions`, `Vara.Application.Backup`, `Vara.Application.History`.
- **CLI Presentation & Commands**: `Vara.Cli.Presentation.BackupProgressColumn`, `Vara.Cli.Composition.ErrorReporting`, `Vara.Cli.Commands.ProfilesCommand`, `Vara.Cli.Commands.DiffCommand`, `Vara.Cli.Commands.ShowCommand`.
- **Test Projects**: `Vara.Application.Tests`, `Vara.Cli.Tests`, `Vara.IntegrationTests`. Test harnesses and fakes updated to exercise new type shapes and unions.
- **File Organization**: Several new dedicated files created to isolate closed hierarchies from presenter classes, maintaining modularity.
- **Language / Tooling**: Prerequisite already met (`net11.0` and `<LangVersion>preview</LangVersion>` in `Directory.Build.props`). No project configuration updates needed.
- **Runtime Performance & Memory**: Reduced heap allocation and memory footprint per operation in `BackupPlan` (32-56 bytes vs 104 bytes per operation) and zero-allocation scanning in `DirectoryFileSystemScanner`. Singleton instances for presentation states avoid any allocation during UI refresh loops. Direct `catch (VaraDomainException)` eliminates exception-filter overhead.
- **Breaking Changes**: None to external CLI interfaces or database storage schemas; purely internal code structure and type contracts.
