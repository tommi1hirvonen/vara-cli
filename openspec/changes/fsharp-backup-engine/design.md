## Context

See proposal.md for why. The design starts from the F# skeleton set up outside the change series, and from the findings of an exploratory spike whose code was not kept.

The skeleton (in the main tree and in `Vara.slnx`):

- `src/fsharp/Directory.Build.props`: F# 11 (not preview), `--reflectionfree`, `FS0025;FS3261;FS3511` as errors, and the explicit `FSharp.Core` reference that central package management requires. `tests/fsharp/Directory.Build.props` adds the shared test packages.
- `Vara.Domain`: `Outcome` and its exit-code mapping.
- `Vara.Engine`: the `Flow` combinators (`parallelEmit`, `foldChannel`, `attempt`).
- `Vara.Cli` (`vara.exe`, Native AOT): the command tree, the `CommandAction.set` template (a handler returns an `Outcome`, and an escaping exception is reported in the hard-error style), the two-stage Ctrl+C, and the outcome styles. It publishes with zero IL2xxx/IL3xxx warnings at 3.1 MB.
- One test project per source project, with FsCheck in `Vara.Domain.Tests` and `Spectre.Console.Testing` in `Vara.Cli.Tests`.

What the spike established (its benchmark results are in `benchmarks/baselines/2026-11-08-csharp-vs-fsharp-slice`):

- **AOT:** the current package set works under Native AOT with `--reflectionfree`, as do `DllImport` with `SafeFileHandle`, `Channel` with `Parallel.ForEachAsync`, `Utf8JsonWriter` and the YamlDotNet DOM.
- **A faithful port** of the scan and backup slice reached behavioral and allocation parity with C#, but no structural simplification.
- **A functional redesign** of the same slice (a scan stream, a pure classification, and execution as a fold, with a thin shell around them) passed the same parity suite and allocated within about +1% of C#.
- **Benchmarks** comparing C# and both F# variants side by side: time on the development machine is dominated by antimalware noise, so allocations are the reliable signal there.

Constraints that shape this design:

- The `backup-execution` spec is the behavioral contract.
- `profile.db` and the target layout must stay interchangeable with the C# build, because users' existing targets must keep working after the cutover.
- The C# app stays the shipped product until the final change in the series.

## Goals / Non-Goals

**Goals:**

- Establish the functional architecture and conventions for the engine-shaped parts of the code, which later migration changes follow.
- Deliver a complete F# backup engine: every `backup-execution` scenario the engine owns is verified by a test named after that scenario.
- Keep performance parity measurable on every change: an allocation budget, plus advisory time measurements.
- Run the engine inside the published Native AOT `vara.exe` through `vara backup`, so it is free of AOT warnings from its first change.

**Non-Goals:**

- Backup presentation: live progress, the run-summary layout, JSON output, sleep prevention and the dry-run display. These belong to `fsharp-backup-presentation`. `vara backup` prints an interim plain-text summary until then.
- Profile YAML loading and validation, which come from `fsharp-profile-config`.
- History, restore, browse, prune and check. These have their own changes, although the manifest module keeps its schema ownership here.
- Behavior changes. Any intended behavior change needs its own change with a spec delta and must not ride along with the migration.

## Decisions

### D1. The migration is a series of changes on top of a shared skeleton

There is one change per group of capabilities, each preserving behavior (`skip_specs: true`) and each leaving both builds green. The project layout, build settings and host conventions were set up once, outside the series, so no change has to invent them. A single big change was rejected: it would be unreviewable, and its design for history or profiles would have to be guessed before the engine had taught us anything. See "Migration roadmap" below for the sequence and its decision gates.

### D2. The skeleton's layering: a pure core, an imperative shell, and a host

| Project | Contains | May reference |
|---|---|---|
| `src/fsharp/Vara.Domain` | domain types; scan classification; diff; plan resolution; the execution state machine; manifest-row construction; progress projection | `FSharp.Core` only |
| `src/fsharp/Vara.Engine` | directory walking; the content store; the SQLite manifest; kernel32 interop; the run lock; the `Flow` combinators; composition of the backup run | `Vara.Domain` and the infrastructure packages |
| `src/fsharp/Vara.Cli` | the `backup` command: argument parsing, profile resolution, the interim plain-text summary | `Vara.Domain`, `Vara.Engine`, System.CommandLine, Spectre.Console |

The skeleton already has this layout with its final names; the projects move from `src/fsharp/` to `src/` at the cutover. A project reference cannot stop code from calling `System.IO`, but keeping infrastructure packages out of `Vara.Domain` makes accidental effects visible in review. The domain tests also run without touching the disk. The `backup` command follows the skeleton's `CommandAction.set` template, so it maps a refused run or a run with failed paths to an `Outcome` rather than to an exit code of its own.

Alternatives considered:

- **A single project, layered by F# file order:** rejected. Nothing would stop a decision function from calling into the manifest.
- **Mirroring the four C# layers:** rejected. `Vara.Core`/`Vara.Application` is mostly a split the C# compiler needs; in F#, compile order already enforces dependency direction inside each project.

### D3. Functional core, imperative shell: decisions are pure, effects run at the edge

This decision fixes constraints, not names. The concrete modules and types are designed in the first task (2.1), from the spec and these constraints:

- Every decision the spec describes is a pure function in `Vara.Domain` that can be tested with plain values:
  - which scanned entries are reported (excludes, globs, mirror-path mapping);
  - how an entry compares with its manifest row (added, changed, unchanged, link);
  - which tracked paths count as deleted, given the scan failures;
  - which added files need content signatures to rule move candidates in or out, and how moves are resolved from those signatures, including the tie-break by deleted-path order;
  - how the run's state evolves as operations complete, which manifest rows each completion produces, when a checkpoint is due (with the clock passed in), the progress projection, and the final outcome;
  - the snapshot summary and the choice between cancelling and completing.
- `Vara.Engine` performs I/O in the order the spec fixes: walk, read signatures, store, place, record, commit. Beyond sequencing that I/O and translating I/O exceptions into the domain's failure values, it holds no decision logic.
- Move detection reads files in the shell before planning, so that planning itself is pure. The C# planner reads the files itself.
- The chosen representation of the run's state is checked against the allocation budget (D6) before the shell is built, because it determines the allocations per file.

Alternatives considered:

- **A free monad or interpreter over all I/O:** rejected. It allocates per step, it is harder to reason about under AOT, and it obscures a pipeline whose I/O order is fixed by the spec.
- **Keeping the C#-shaped executor** (mutable counters, a record of callbacks, a planner that reads files): rejected. The spike measured that shape in F# and it gained nothing.

### D4. Effects are passed in as records of functions

Effects that tests need to vary (hard-link creation, the clock, the checkpoint interval, the transfer concurrency) are passed in as functions. A step that needs one effect takes that function as a parameter, not a record. Effects that always travel together (three or more) are grouped into a record of functions, for example `Links = { Create: string -> string -> unit }`. The composition root assembles them once, by partial application. This replaces the C# test hooks (`ForceNextHardlinkFailureForTesting`, `ForceHardlinkSupportForTesting`). Interfaces, classes with injected dependencies and containers were rejected because nothing here needs more than a function.

### D5. Expected outcomes are values; unexpected failures are exceptions

- **Busy target:** the backup run's entry point returns a `Result` whose error case is a refusal: the target is busy, or access to the run lock is denied.
- **Per-operation I/O failures** (`IOException`, `UnauthorizedAccessException`, or a mirror path that escapes the target root): turned into the domain's per-operation failure values at the shell boundary, exactly where C# catches them.
- **Anything else** (SQLite failures, programming errors): stays an exception. It is recorded as a failed snapshot and then rethrown with its original stack through `ExceptionDispatchInfo`. `reraise()` is not allowed inside `task {}`, and that restriction holds even with `LangVersion preview`.

Profile and configuration failures follow the same shape in `fsharp-profile-config`: one `ConfigError` union and one function that renders it. The refusal type here should do the same.

Railway-oriented `Result` chaining through all I/O was rejected. It would wrap every file call without adding information that the per-operation failure values do not already carry.

### D6. Data and performance rules, with an allocation budget

These are already codified in the skeleton's `src/fsharp/Directory.Build.props`:

- `--reflectionfree`;
- `FS0025`, `FS3261` and `FS3511` as errors (incomplete matches, nullness, and a task that is not compiled to a state machine);
- `LangVersion` 11, not preview, so that F# preview-only features are not used.

Coding rules:

- Use struct DUs and records for per-entry values, and `voption` rather than `option`.
- Compare structs with `.Equals`, never with `=`, which boxes.
- Avoid closures that capture locals in per-entry or per-chunk paths.
- Use interpolated strings only; no `printf` family functions.
- Mutation is allowed only as a local inside one function (a loop counter, a pooled buffer, interop), and only where the allocation budget or the API needs it. It never lives in a field and never crosses a function boundary. The pure fold is the default, and a local `mutable` needs a measured reason that is recorded in the commit message.
- Two things learned under `--reflectionfree`: `ToString` on a `Result` prints only its type name, so tests that compare results need their own `show` function; and a `let mutable` cannot be captured by a closure, so tests use a `ref` cell or a fold.
- These rules change a value's representation, not the architecture. They never justify classes, shared state or an object that holds the engine's dependencies.

Budget: every `LanguageComparisonBenchmarks` case may allocate at most 5% more than C#. Time is advisory on this machine; an AOT time comparison on a quiet machine is part of `fsharp-backup-presentation` (Gate B).

### D7. Concurrency: bounded parallel transfers feeding a single recorder

The spec's checkpoint and cancellation requirements need exactly one place that records completed operations and owns the manifest connection, while transfers run with a bounded degree of parallelism. The runtime primitives stay `Channel` and `Parallel.ForEachAsync`: they are task-based, AOT-safe, and already what the C# engine uses. The skeleton's `Flow` combinators wrap them, but they are not binding; the engine may reshape or replace them if its design calls for a different form.

Alternatives considered:

- **`MailboxProcessor`:** rejected. It is based on `Async`, it allocates per message, and it cannot keep up with chunk-level progress updates.
- **TaskSeq or Rx:** rejected. They add a dependency where a few dozen lines suffice.

### D8. The specs are verified by name; parity is verified by comparison

The engine's tests have four layers:

1. **Scenario tests.** Each `backup-execution` scenario owned by this change has one test whose name is the scenario title. The tests are grouped by requirement.

   This change owns every scenario except those of the two sleep-prevention requirements, which move to `fsharp-backup-presentation` together with the rest of the backup command's presentation. It owns:
   - the three "Backup command accepts profile as a positional argument" scenarios, because `vara backup` is added here;
   - the four dry-run scenarios, through `vara backup --dry-run` and its interim summary;
   - "Second Ctrl+C forces immediate termination", which is driven through the skeleton's `GracefulCancellation`.

   Coverage is checked with the shared check in `tests/fsharp/Vara.Testing`, from `fsharp-profile-config`.
2. **Parity tests.** The C# and F# engines run on the same sources, and their manifest rows, snapshots, mirror contents, read-only flags and blobs are compared. Beyond the happy path, they cover:
   - locked and unreadable files and directories;
   - a file modified during its own transfer;
   - targets without hard-link support;
   - the hard-link limit;
   - cancellation;
   - a recorder crash;
   - legacy-collation databases;
   - each implementation continuing a target created by the other.
3. **Property-based tests** (FsCheck), checking these invariants of the pure core:
   - every tracked path is either seen, deleted, or hidden by a failure;
   - plan totals equal the sum of transfer sizes;
   - applying completed operations in any interleaving that keeps each file's own order produces the same counts and failed paths;
   - move resolution never consumes a deleted row twice.
4. **Native AOT.** `vara.exe` is published with Native AOT and runs a first and an incremental backup through `vara backup`. No separate probe executable is needed: the AOT compiler only analyzes code that is reached, so the real command is the meaningful check.

Parity tests reference the C# projects and are removed at the cutover. Layers 1, 3 and 4 remain as the long-term suite.

### D9. The engine is written from this design, not harvested from the spike

Only the spike's `Flow` combinators were carried into the skeleton, and D7 leaves them open to change. The rest of the spike's code was discarded. Its port was C#-shaped by design, and its functional slice was still constrained by C#'s I/O sequence and by being called from C#, so neither is a reference for the engine's design. The engine is written fresh from D3 against the spec, and the parity suite and the comparison benchmarks are written fresh in `Vara.Engine.Tests` and `benchmarks/Vara.Benchmarks`. The spike's lessons are encoded in D5 and D6 instead:

- `reraise()` is not allowed in `task {}`;
- `=` on structs boxes;
- record types that share field names need type-qualified construction;
- calling F# APIs from C#, which the benchmarks and parity tests do, needs `FuncConvert` and `FSharpValueOption`.

The spike's recorded run, `2026-11-08-csharp-vs-fsharp-slice`, is committed according to `benchmarks/baselines/README.md` (HTML removed, row added) as the evidence behind this series.

### D10. On-disk compatibility is part of the contract

The engine keeps:

- the C# SQLite schema, ISO-8601 timestamps and change-kind names;
- the legacy `COLLATE NOCASE` migration;
- the `.vara/versions/<hash[0..2]>/<hash>` layout;
- staging under `.vara/tmp`;
- the `.vara/run.lock` file.

A target written by either build must be continued by the other without re-transfers. This is already tested, and stays in the parity suite until the cutover.

## Migration roadmap

The skeleton (projects, build settings, `Outcome`, `Flow`, the command template, Ctrl+C, AOT publish) exists before the series and is not a change of its own.

| # | Change | Capabilities | Exit criteria |
|---|---|---|---|
| 1 | `fsharp-profile-config` | profile-config | Every scenario tested. **Gate A:** the rule-heavy code is clearly simpler than the C# version, and idiomatic F# rather than a port, in the reviewer's judgment, backed by line and invariant counts and the convention review |
| 2 | `fsharp-backup-engine` (this) | backup-execution (engine and `vara backup`) | D8 layers 1–4 green; allocation budget held |
| 3 | `fsharp-backup-presentation` | cli-presentation and progress-reporting (backup and dry run); the rest of backup-execution | **Gate B:** on a quiet machine, AOT wall-clock time within ±5% of C#; peak memory and executable size recorded and within the agreed margin; zero AOT warnings |
| 4 | `fsharp-snapshot-history` | snapshot-history, backup-browsing, restore progress | Every scenario tested |
| 5 | `fsharp-retention-and-integrity` | retention-pruning, backup-integrity | Every scenario tested |
| 6 | `fsharp-profile-management` | profile-management (interactive editor) | Every scenario tested |
| 7 | `fsharp-cutover` | portable-distribution | F# `vara.exe` is shipped; C# projects, tests and parity suite removed; folders moved to `src/`; benchmark baseline reset |

Profile configuration comes first for two reasons: `vara backup` needs profiles loaded from YAML, and Gate A, the cheaper of the two decisions, is then reached before the largest investment.

If either gate fails, the series stops. `src/fsharp` and `tests/fsharp` are deleted, and the transferable patterns are backported to C# as ordinary changes. These include scanning as a stream, the probe-gated content store, progress built only when something observes it, and plan totals computed once.

## Risks / Trade-offs

- [The two implementations drift during the migration] → C# behavior is frozen except for bug fixes. Every fix lands with a parity test, so that the F# side must follow.
- [An immutable state raises allocations on hot paths] → The state's representation is chosen with per-file allocations in mind and reviewed in task 2.1 (D3), and the per-change allocation budget (D6) catches regressions.
- [AOT or FSharp.Core issues surface late] → `vara.exe` is published with the engine reachable in this change, and every later change publishes it again with its own commands reachable.
- [Pre-GA toolchain] → The SDK and FSharp.Core are pinned to .NET 11 RC3 (`11.0.100-rc.3.26522.148`, FSharp.Core `11.0.101-rc3.26522.148`) while the change is implemented; they move to .NET 11 GA (10 November) when it ships, in a separate commit. Known limitations (no `reraise()` in `task {}`; FSharp.Core must be referenced explicitly under central package management) are handled in code and in `Directory.Packages.props`.
- [Benchmark noise from antimalware on the development machine] → Allocations are the per-change gate. Time is gated once, at Gate B, on a quiet machine.
- [The scenario tests are only as good as their mapping] → One test per scenario, named after the scenario title, makes gaps mechanically checkable: compare the list of scenario titles with the list of test names.
- [Parity tests vanish at the cutover] → By then every scenario has its own F# test; parity only guards the transition.

## Migration Plan

Nothing ships in this change. The C# build stays the distributed `vara.exe`; the F# `vara.exe` is published only to `artifacts/` for verification. To roll back, revert this change's commits; the skeleton and `profile-config` work stay intact. Neither build depends on the other at runtime.

## Open Questions

- Whether parity tests are kept in some form after the cutover, for example against a recorded corpus. This is decided in `fsharp-cutover`.
