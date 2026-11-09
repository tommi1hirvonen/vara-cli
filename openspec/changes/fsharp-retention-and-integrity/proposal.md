## Why

Retention pruning keeps the target's history bounded, and the integrity check verifies that stored content still matches the manifest. Both are maintenance commands over the same manifest and content store.
- **Prune** is driven by a tiered retention policy: daily, weekly, monthly and yearly buckets. That is a rule-heavy, naturally pure computation, and it follows from the profile model built in `fsharp-profile-config`.
- **Check** is a bounded-parallel read workload, with count-based progress.

Design and tasks for this change are written by `fsharp-snapshot-history`'s final task, using the findings of the changes before it.

## What Changes

- Add `vara prune`:
  - a pure retention evaluation that decides which snapshots to keep and which to prune;
  - pruning of manifest rows that never removes a path's current state;
  - garbage collection of content that is no longer referenced;
  - the run lock, progress, and graceful cancellation.
- Add `vara check`: full and quick verification of stored content against the manifest, with a scriptable exit status, progress with an ETA, and graceful cancellation.
- Add the count-based progress display used by both commands.
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

**Established by `fsharp-snapshot-history`** (apply these in addition to the rules above)
- **Commands are pipelines over shared stages.** `HistorySession` holds the stages (`resolve` the profile, `withManifest`, `show` a `Display`, `fail` with a message, and the option checks `exactlyOne`, `mutuallyExclusive` and `firstRefusal`, whose checks run lazily because a refusal reports itself when it is made). A command module composes them with its own projection and extends nothing.
- **A destructive operation is a plan, then an execution.** A pure function returns the plan as data (`RestorePlan`); the guard and the confirmation read it; the shell performs it (`RestoreRun.execute`) through a record of functions (`RestoreEnv`: open a blob, `ShouldStop`, publish a `ProgressEvent`). A stop is read between items, never inside one, and what was applied stays applied. A per-item I/O failure is a value (`RestoreFailure`) that the run records and continues past; any other exception is rethrown with its stack (`reraise ()` in synchronous code). Loops over items are a tail-recursive fold (`applyAll`), not a `let mutable` counter.
- **Readers are read-only.** `SqliteManifest.openReadOnly` opens with `Mode=ReadOnly`, runs no pragma and creates no table, so a reader never changes `profile.db` and never creates one; a target without a manifest reads as empty. Queries return rows lazily (`allVersions`) or small values, and the domain folds them, so memory follows the number of distinct paths, not rows. A writer (a backup, a prune) uses `openAt`.
- **Prompts are a record of two functions.** `Prompts.Select title choices defaultIndex` returns an index (each choice is a list of styled `Span`s, so a status can be coloured); `Prompts.Confirm question` defaults to no. Only the `Prompts` module calls Spectre prompts. A session can ask a question when `Capabilities.InputIsInteractive` holds (and, for a picker, the output is live: `HistorySession.canPrompt`). A test passes scripted functions and records the questions; `noPrompts` fails a test that asks one.
- **More effects live in `CliEnv`.** `CurrentDirectory` (read once; paths are resolved against it), `UtcOffsetAt` (a date without an offset is in the local zone) and `OpenStandardOutput` (raw bytes for `show`). Tests replace fields, never the console's type.
- **The display vocabulary grew.** `Line.Spans` (mixed tones), `Line.Table` (aligned columns, a rounded border) and `Line.Box` (a titled frame), `Tone`s for the change kinds, and `Display.plainLines` (the unstyled text, a pure function that tests read). `Display.plainLinesWith` takes the border glyphs (rounded, or ASCII on a console that cannot show them) and `Display.mapTexts Display.toAscii` replaces the tree guides; both renderers apply them from `Profile.Capabilities.Unicode`. Plain output is written whole, never wrapped at the console's width.
- **The progress bracket takes a publisher.** `RunProgress.withProgress` gives the run a function that publishes `ProgressEvent`s; the backup maps them to the engine's observers (`BackupCommand.observersFor`), a restore publishes them itself. `Screen` carries a `Wording` (the two labels) and the initial `Tracker`, so a run whose total is known starts in the transfer phase at 0%. A display too short to read is not shown (`ProgressDisplay.worthShowing`).
- **Date and path input are parsers with friendly errors.** `DateInput.parse offsetAt optionName text`, `DateInput.help` for every date option's description (the example comes from one constant) and `PathInput.resolve` for the flexible path forms, all pure, with the machine's answers (`IsWithinMirror`, `hasHistory`) passed in.
- **No `let mutable` in `Vara.Domain`.** A flag or a counter is a fold, a `Seq.exists` or a struct accumulator. A dictionary filled inside one function is allowed when a benchmark or the row count needs it.
- **Native AOT:** `Seq.init` and `Seq.initInfinite` raise IL2091 from FSharp.Core; use an array range (`[| 0 .. n - 1 |]`). Spectre prompts, `Table`, `Panel` and DiffPlex publish without IL warnings.
- **Test pitfalls found.** A module name cannot contain `/`, `+` or a backtick; the coverage helper ignores them when it compares names. Some files in the repository have CRLF line endings: an edit script must keep each file's style. `Assert.DoesNotContain` on text with an escape character needs `Contains(char 27)`. A file fingerprint test checkpoints the write-ahead log through a non-pooled connection first. Under heavy parallel load an existing SQLite test can fail with `ObjectDisposedException` on `SQLitePCL.sqlite3` because test helpers clear all pools; rerun it alone and report the flake.
- **Parity of text.** A redirected console is 80 columns wide, so the C# build folds and shortens table cells, and it formats dates with the current culture (F# uses the invariant culture). Parity tests compare projected rows (an id and a kind, a name and a status, a size) and counts, or the characters of a folded report, never the layout. The F# restore also sets a file's modification time to the source's; the C# restore does not, so times are not compared.
- **Benchmarks only when a hot path changed.** `LanguageComparisonBenchmarks` and `Measure-GateB.ps1` are rerun only when the scan, the executor or the pipeline changed; otherwise a task records that `git diff` over those modules is empty. Allocation ratios against C# (5% budget) remain the reliable signal.

**Specific to this change**
- Retention evaluation is a pure function from the snapshot list, the policy and the current time to a keep set and a prune set. Each tier (daily, weekly, monthly, yearly) is a small bucketing function, and the policy is their composition. The result does not depend on input order.
- Pruning is a plan and then an execution: a pure plan of rows to remove and content to release, computed as set differences, then performed by the shell under the run lock. The same plan serves any preview.
- Check is a stream of entries mapped through a pure verdict function (`entry -> observed content -> Verdict`), folded into a summary and an `Outcome`. The shell reads the content in parallel; the verdict never reads.
- Count-based progress reuses the `Display` view from `fsharp-backup-presentation`, with no separate progress classes.

## Capabilities

### New Capabilities

None.

### Modified Capabilities

None. This is a behavior-preserving reimplementation (`skip_specs: true`).

**Owned scenarios:**
- **`retention-pruning`:** every requirement.
- **`backup-integrity`:** every requirement.
- **`progress-reporting`:** "Count-based progress indication for non-byte-transfer operations".
- **`profile-config`:** "Profile without a retention policy".

## Impact

- **Code:** all three F# source projects grow. No changes to the C# projects.
- **Tests:** scenario tests, FsCheck properties for retention evaluation (for example, a snapshot kept by any tier is never pruned, and the pruned set does not depend on input order), and parity tests that run the C# and F# commands against the same targets and compare the remaining manifest and blobs.
- **Dependencies:** requires `fsharp-snapshot-history`, for the manifest queries and presentation it adds.
