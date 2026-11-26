# Contributing

## Development

The solution targets **.NET 11** (`net11.0`) and **F# 11**; install the .NET 11 SDK to
build, test, or publish it. Every command below runs from the repository root.

```
dotnet restore Vara.slnx
dotnet build Vara.slnx
dotnet test Vara.slnx
dotnet publish src/Vara.Cli -c Release -r win-x64 -o artifacts/cli
```

The published `artifacts/cli/vara.exe` is the Native AOT build described under
[Installation](README.md#installation). `dotnet build` is expected to report no warnings;
the F# projects treat an incomplete match, a nullness error and a task that is not compiled
to a state machine as errors.

Layout:

```
src/
  Vara.Domain/        pure decisions, data and views
  Vara.Engine/        file system, SQLite, hashing, YAML, the runs
  Vara.Cli/           composition root, commands, rendering (publishes vara.exe)
tests/
  Vara.Domain.Tests/  scenario and property tests of the domain
  Vara.Engine.Tests/  engine, configuration and corpus tests
  Vara.Cli.Tests/     command tests, in process and as processes
  Vara.Testing/       spec-coverage check, corpus and anonymizing helpers
benchmarks/           BenchmarkDotNet scenarios, baselines, release check script
openspec/             specifications and the history of the changes that made them
```

Tests are named after the specification: a module is named after a requirement and a test
after one of its scenarios, and the spec-coverage tests fail when a scenario of any spec in
`openspec/specs` has no test.

**Recorded corpus.** `tests/Vara.Engine.Tests/Corpus` holds golden files: the expected
results of the configuration loader (including files exactly as the profile editor writes
them), the retention decisions around calendar boundaries, the backup engine's scenarios,
and the manifest migration. `unversioned` is a complete target written before the manifest
recorded a schema version, which the engine must keep reading, because users keep their
targets across releases. The files are data (marked `-text` in `.gitattributes`, so their
bytes are never converted) and are compared with the actual results by plain functions.
Nothing regenerates them: a corpus file is changed only when the behavior it records is
changed on purpose, in the same change as the code.

**Benchmarks.** Run the BenchmarkDotNet scenarios in Release mode:

```
dotnet run -c Release --project benchmarks/Vara.Benchmarks -- --list flat
dotnet run -c Release --project benchmarks/Vara.Benchmarks -- --filter '*StreamingSignatureBenchmarks*'
dotnet run -c Release --project benchmarks/Vara.Benchmarks -- --filter '*' --job short
```

See [the benchmark guide](benchmarks/Vara.Benchmarks/README.md) for scenario coverage and
measurement guidance, and [the recorded baselines](benchmarks/baselines/README.md) for the
reference results. Allocations and CPU time are the figures to trust; wall-clock time on a
developer machine is noisy. `benchmarks/Measure-ReleaseCheck.ps1` is an optional release
check of the published `vara.exe` (CPU time, peak memory and size) against the recorded
baseline; see [the baselines](benchmarks/baselines/README.md) for how to run it.

### Code conventions

The code is idiomatic F#, designed from the specifications. These rules apply to new code:

- **Modules and data, not classes.** Behavior is functions in modules; data is records and
  unions. No interfaces, inheritance, service classes or dependency injection. A class is
  allowed only where .NET requires one: handle wrappers, `IDisposable` resources, streams,
  and types that a host library demands (such as a BenchmarkDotNet scenario).
- **Dependencies are parameters.** A function takes the functions it needs, not an object
  that holds them. Effects that travel together may be grouped as a record of functions,
  when three or more are used together (`Links`, `ExecutionEnv`, `BackupEnv`, `CliEnv`).
  `CliEnv.live` defines the live CLI dependencies; `Program.main` partially applies
  `Composition.withCommands` to that environment, and `Composition` passes it to command
  factories. Invocation-specific dependencies, such as a backup environment tied to the
  command's cancellation token, are created when that command runs.
- **Compose, don't sequence.** Build behavior from small functions joined with `|>`,
  `Result`, `Option` and `Seq` combinators, and a computation expression where a flow
  reads in order. Put the data argument last, so functions can be partially applied when
  useful.
- **Types carry the rules.** Unions for alternatives, `[<RequireQualifiedAccess>]` on
  them, records with named fields, single-case unions or units of measure for values that
  could be confused. Matches on the code's own unions are exhaustive, with no wildcard; no
  `null`; invalid states are unrepresentable.
- **Expected outcomes are values.** Use `Result` or `option` for them. Failures have three
  levels: a refusal to start is a `Result` error, a per-operation I/O failure is a value
  that the run records and continues past, and anything else is an exception that is
  recorded and rethrown with its stack (`ExceptionDispatchInfo` in `task {}`) and handled
  at the host boundary.
- **Pure core, effect shell.** Decisions in `Vara.Domain` never touch the file system, the
  clock, randomness or the console. The shell passes in values and performs the effects.
  A destructive operation is a plan, computed by a pure function, and then an execution.
- **Presentation is a pure view.** A command builds a `Display` (plain data: lines,
  fixed-width cells, tones) in `Vara.Domain`; one interpreter per output (`LiveRenderer`,
  `PlainRenderer`, JSON) writes it. Spectre.Console types appear only in the renderers,
  `Prompts`, `Terminal`, `CliEnv`, `CommandAction` and `ErrorReporting`.
- **Interactive commands are Elm-style loops.** State and input are unions, `update` and
  `view` are pure, and the thin loop around them asks, calls `update` and performs the
  effects it returns (`ProfileEditor`).
- **Mutation stays inside one function.** It is allowed in a hot loop, a pooled buffer or
  interop, when a benchmark or the API requires it, with the reason in the commit message.
  It never lives in a field, is never shared between functions, and does not appear in
  `Vara.Domain`. Allocation rules (struct unions, `voption`) change a value's
  representation, not the architecture.
- **No catch-all modules.** No `Helper`, `Manager`, `Service`, `Util` or `I`-prefixed
  names. One module per concept, with files in dependency order.
- **Native AOT.** `Seq.init` and `Seq.initInfinite` raise IL2091 from FSharp.Core; use an
  array range (`[| 0 .. n - 1 |]`). The publish must report no IL2xxx or IL3xxx warnings.
- **Tests.** Test names follow the specification. A test replaces an effect with a function
  (a scripted prompt, a field of `CliEnv`, a function that raises), never a console type.
  Every SQLite user in a test clears the connection pool when it closes. The tests run in
  parallel; if one of them fails only under load (a SQLite `ObjectDisposedException` on a
  disposed handle is the known case), rerun it alone and report the flake.

This project's specifications and design decisions are tracked with
[OpenSpec](openspec/) under `openspec/specs/` (current, authoritative capability specs)
and `openspec/changes/archive/` (the historical proposals that produced them) - useful
background reading, but the code itself remains the ultimate source of truth for actual
behavior.
