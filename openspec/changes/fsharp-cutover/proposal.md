## Why

Once every capability has an F# implementation that passes its scenario and parity tests, the F# `vara.exe` can replace the C# build. Until then, two implementations are maintained, C# changes are frozen except for bug fixes, and each fix needs a parity test. Ending that cost is the point of this change.

Design and tasks for this change are written by `fsharp-profile-management`'s final task, using the findings of the whole series.

## What Changes

- The F# `vara.exe` becomes the shipped build, and the `portable-distribution` scenarios are verified on it.
- **BREAKING (repository layout only):**
  - remove the C# projects (`Vara.Core`, `Vara.Application`, `Vara.Infrastructure`, `Vara.Cli`) and their test projects;
  - move `src/fsharp/*` to `src/` and `tests/fsharp/*` to `tests/`;
  - update `Vara.slnx`, `Directory.Build.props` and `Directory.Packages.props`, dropping the C#-only settings and packages.

  Users see no change: the executable, commands, output, configuration and on-disk format are the same.
- Decide what happens to the parity suite, which references the C# projects: remove it, or replace it with a recorded corpus of expected outputs. Scenario and property tests remain the long-term suite.
- Move the benchmarks to the F# implementation and record a new reference baseline. Remove `LanguageComparisonBenchmarks` and the `benchmarks/Vara.Benchmarks.FSharp` adapter, which exist only to compare the two builds, and decide whether `benchmarks/Measure-GateB.ps1` stays as an optional manual release check (its CPU-time, memory and size rules can be asserted; its wall-clock numbers are advisory). Update `benchmarks/baselines/README.md`.
- Update `README.md` and the build and publish instructions.
- Archive the series' changes as needed, and end the C# behavior freeze.

## Functional steering

This change is idiomatic F#, not a translation of the C# code. C# type and method names are not a guide: design from the spec. Each change in the series is applied in a separate session, so every proposal carries these rules.

**Series-wide rules**
- **Modules and data, not classes.** Behavior is functions in modules; data is records and unions. No interfaces, inheritance, service classes or dependency injection. A class is allowed only where .NET requires one: handle wrappers, `IDisposable` resources, and types that a host library demands.
- **Dependencies are parameters.** A function takes the functions it needs, not an object that holds them. Effects that travel together may be grouped as a record of functions, only when three or more are used together. Everything is assembled once, by partial application in the composition root (`Vara.Cli`).
- **Compose, don't sequence.** Build behavior from small functions joined with `|>`, `Result`, `Option` and `Seq` combinators, and a computation expression where a flow reads in order. Put the data argument last so that partial application works.
- **Types carry the rules.** Unions for alternatives, `[<RequireQualifiedAccess>]` on them, records with named fields, single-case unions or units of measure for values that could be confused. Matches are exhaustive with no wildcard on the code's own unions; no `null`; invalid states are unrepresentable.
- **Expected outcomes are values.** Use `Result` or `option` for them. Exceptions are for the unexpected, and are handled at the host boundary.
- **Pure core, effect shell.** Decisions never touch the file system, the clock, randomness or the console. The shell passes in values and performs the effects.
- **Mutation stays inside one function.** It is allowed in a hot loop, a pooled buffer or interop, when a benchmark or the API requires it. It never lives in a field and is never shared between functions. Allocation rules (struct unions, `voption`) change a value's representation, not the architecture.
- **No catch-all modules.** No `Helper`, `Manager`, `Service`, `Util` or `I`-prefixed names. One module per concept, with files in dependency order.
- **Review.** The change's tasks include a convention review.

**Established by `fsharp-backup-engine`** (apply these in addition to the rules above)
- **Failures have three levels.** A refusal to start is a `Result` error. A per-operation I/O failure is a value (`Completion.Failed`) that the run records and continues past. Anything else is an exception: record it, then rethrow it with its original stack through `ExceptionDispatchInfo` (there is no `reraise()` in `task {}`).
- **Effects are functions or small records of functions** (`Links`, `ExecutionEnv`, `BackupEnv`, `Observers`), built once in the composition root. A test passes a function that raises in place of a test hook. The C# build is reached from tests by reflection and decorators, never by editing its source.
- **Decision functions in `Vara.Domain` are folds** over immutable values, with struct accumulators where per-entry allocation matters. `let mutable` appears only in reader loops, stream state and interop, each with a measured reason in the commit message; a closure that needs a cell uses a `ref` (a `let mutable` cannot be captured).
- **Per-entry values are struct unions and `voption`.** Compare structs with `.Equals`.
- **Tests are named after the spec.** A module is named after the requirement and a test after the scenario; `+` is not valid in a module name and is ignored when names are compared. The spec-coverage test fails when a scenario has no test.
- **`--reflectionfree` costs:** `ToString` on a `Result` prints only its type name, so comparing tests need their own `show`.
- **Cleanup in tests:** every SQLite user clears the connection pool when it closes, and the shared temp-directory helper clears all pools before deleting, so that one engine's pooled connection cannot lock another's database.

**Established by `fsharp-backup-presentation`** (apply these in addition to the rules above)
- **Presentation is a pure view.** A command builds a `Display` (plain data: lines, fixed-width cells, `Tone` roles) in `Vara.Domain`; `Display.toneOf` maps an `Outcome` to its tone. Progress state is the immutable `Tracker`, advanced by `Tracker.update` with a clock value passed in; a heartbeat is `Display.view` at a later clock value. Nothing in the domain references Spectre.Console.
- **One interpreter per output.** Spectre.Console types appear only in `LiveRenderer` (the live region), `PlainRenderer` (unstyled text), `Terminal` (styles, the console edge) and the `CliEnv` record. `OutputMode.choose json capabilities` picks `Json`, `Live` or `Plain` from `Capabilities`, which the composition root reads once. Plain output never carries a control sequence.
- **Commands run against `CliEnv`**, a record of effects (consoles, capabilities, JSON writer, run environment, power calls, `WT_SESSION`), built once in `CliEnv.live`. A command adds the effect functions it needs to that record. Tests replace fields; they never replace the console's type.
- **Progress goes through one bracket.** `RunProgress.withProgress` takes a `Screen` (a record of functions: draw, width, write line, write indicator, clock, heartbeat), installs observers that publish `ProgressEvent`s, redraws on admitted updates and on a timer, draws the final state before teardown, and clears the display and the Windows Terminal indicator in a `finally`. A late report after teardown is ignored. A run that throws is shown as a hard error before the exception continues.
- **Output is data, then written once.** `--json` is a `JsonValue` built by a pure function and written by `JsonOutput.write` to the writer in `CliEnv`, never through the console. Text that carries numbers is formatted with the invariant culture.
- **Constants come from the C# build and say so.** Layout widths, intervals and label texts that the spec does not fix are named constants with a comment (`Display.fs`, `Tracker.redrawInterval`).
- **Test pitfalls found.** `TestConsole` is not a terminal: force the live mode through `CliEnv.Capabilities` and use `.EmitAnsiSequences().Interactive()`. Culture-sensitive string asserts ignore control characters, so look for an escape with `Contains(char 27)`. The runtime escapes a comma in a module name as `\,`, and the spec-coverage helper reads it back. Hard-linked mirror entries are read-only, so test and benchmark cleanup clears the attribute before deleting.
- **Parity tests run both builds as processes** (`dotnet vara.dll`, the C# project referenced with `ReferenceOutputAssembly="false"` because both assemblies are named `vara`) and compare normalized JSON, exit codes and the counts of the text summary. A graceful Ctrl+C cannot be delivered to a child process, so cancellation parity rests on the engine parity tests plus an in-process test of the F# command.
- **Benchmark timing on this machine is noisy.** `benchmarks/Measure-GateB.ps1` measures the Native AOT builds against each other and flags a scenario whose interquartile range is above 5% as inconclusive. Allocation ratios (5% budget) are the reliable signal.

**Specific to this change**
- This change adds almost no code, so its convention review is a whole-codebase audit against the rules above, run before the C# code is removed. Findings that need design work become ordinary changes; mechanical ones are fixed here.
- The cutover must not leave adapters, shims or compatibility layers that exist only to bridge C# and F#. If the parity suite is kept as a recorded corpus, it is plain data and a comparison function, not a harness class hierarchy.

## Capabilities

### New Capabilities

None.

### Modified Capabilities

None. The shipped behavior is unchanged (`skip_specs: true`).

**Owned scenarios:** every `portable-distribution` requirement, verified on the F# executable copied alone into an empty directory.

## Impact

- **Code:** the C# sources and tests are removed, and the F# projects are moved.
- **Distribution:** the published `vara.exe` is the F# build.
- **Benchmarks:** a new F# reference baseline.
- **Dependencies:** requires every other change in the series.
