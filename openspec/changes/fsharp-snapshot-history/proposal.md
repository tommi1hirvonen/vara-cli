## Why

Snapshot history is how users get their data back. It covers listing snapshots, a file's version history, restoring files and directories, showing and diffing versions, inspecting what a snapshot changed, and browsing a point in time. It is the largest capability, with 811 spec lines, and it reads the manifest and content store the engine writes.

Its decisions are mostly pure functions over manifest rows: which version was live at a given moment, how a move chain links a file's history, what a directory looked like, and what a restore must write or remove. That makes it the second large test of the functional design, after the engine.

Design and tasks for this change are written by `fsharp-backup-presentation`'s final task, using the findings of the changes before it.

## What Changes

- Add the history commands to the F# `vara`: `snapshots`, `snapshot`, `history`, `restore` (a file or a directory, at a version or a date, to the original location or elsewhere), `show`, `diff`, `browse` and `deleted`.
- Add to `Vara.Domain` the pure history model:
  - point-in-time state;
  - move-chain history;
  - directory listings with deleted entries;
  - the snapshot change roll-up;
  - restore planning.
- Add the manifest queries and restore effects to `Vara.Engine`, including atomic restore writes and the guard against writing into the live mirror. Restore uses the engine's read-only manifest mode.
- Wire profile resolution from the working directory, which `fsharp-profile-config` built, into the browsing and restore commands.
- Add date/time option parsing with friendly errors and an example value in the help text.
- As its last task, update the following changes with this change's findings.

There are no user-visible changes: the shipped `vara.exe` is still the C# build.

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
- **Review.** The change's tasks include a convention review, and its last task keeps this section in the proposals that follow, extended with what the change learned.

**Established by `fsharp-backup-engine`** (apply these in addition to the rules above)
- **Failures have three levels.** A refusal to start is a `Result` error. A per-operation I/O failure is a value (`Completion.Failed`) that the run records and continues past. Anything else is an exception: record it, then rethrow it with its original stack through `ExceptionDispatchInfo` (there is no `reraise()` in `task {}`).
- **Effects are functions or small records of functions** (`Links`, `ExecutionEnv`, `BackupEnv`, `Observers`), built once in the composition root. A test passes a function that raises in place of a test hook. The C# build is reached from tests by reflection and decorators, never by editing its source.
- **Decision functions in `Vara.Domain` are folds** over immutable values, with struct accumulators where per-entry allocation matters. `let mutable` appears only in reader loops, stream state and interop, each with a measured reason in the commit message; a closure that needs a cell uses a `ref` (a `let mutable` cannot be captured).
- **Per-entry values are struct unions and `voption`.** Compare structs with `.Equals`.
- **Tests are named after the spec.** A module is named after the requirement and a test after the scenario; `+` is not valid in a module name and is ignored when names are compared. The spec-coverage test fails when a scenario has no test.
- **`--reflectionfree` costs:** `ToString` on a `Result` prints only its type name, so comparing tests need their own `show`.
- **Cleanup in tests:** every SQLite user clears the connection pool when it closes, and the shared temp-directory helper clears all pools before deleting, so that one engine's pooled connection cannot lock another's database.
- **Benchmarks:** a change that touches a hot path reruns `LanguageComparisonBenchmarks` and records the allocation ratios against C# (5% budget) in its commit message.

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
- Each command is a pipeline: resolve the profile, query the manifest, project to a result with pure functions, build a `Display`, render. The commands share these stages and differ only in the projection.
- Point-in-time state, move-chain history, directory listings, the change roll-up and restore planning are pure functions over manifest rows passed in as values. The manifest queries in `Vara.Engine` return rows or a sequence of rows; no query logic hides in the domain code, and no domain logic hides in a query.
- Restore is a plan and then an execution: a pure function produces the list of writes and removals, and the shell performs them. A dry preview of a restore is therefore the plan itself.
- Output follows the view pattern from `fsharp-backup-presentation` (a pure `Display` rendered at the edge). Diffs and listings are built as data, then rendered.
- No `SnapshotService`, `RestoreService` or repository classes. Reuse the engine's manifest functions rather than adding a parallel access layer.

## Capabilities

### New Capabilities

None.

### Modified Capabilities

None. This is a behavior-preserving reimplementation (`skip_specs: true`).

**Owned scenarios:**
- **`snapshot-history`:** every requirement.
- **`backup-browsing`:** every requirement.
- **`cli-presentation`:** "Bordered widgets use a consistent rounded-corner style", "Invalid date/time option input produces a friendly error" and "Date/time options document an example value in their help text".
- **`progress-reporting`:** "Aggregate progress across a multi-file directory restore" and "Restore task labels are bounded to a fixed width".

## Impact

- **Code:** all three F# source projects grow, and DiffPlex is added to `Vara.Cli` (it is already pinned centrally). No changes to the C# projects.
- **Tests:** scenario tests for each owned requirement, and parity tests that run the C# and F# commands against the same targets and compare their output and restored files.
- **Dependencies:** requires `fsharp-backup-presentation`, for the shared presentation conventions and progress rendering.
