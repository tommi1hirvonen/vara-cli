## Why

The F# exploration on the `fsharp` branch showed two things. A faithful F# port of the scan and backup slice matches C# exactly: it allocates within ±1% and produces identical manifests, mirrors and blobs. But it is no simpler, because it is C# design in F# syntax. A functional redesign of the same slice cut mutable state by about 70%. It turned classification, the execution state, manifest rows and progress into pure functions that can be tested without I/O, and allocations stayed within about 1% of C#.

The value of F# is therefore in a ground-up functional design, not in a port. This change builds the part that carries the most risk: the backup engine (scan, diff, plan, execute). It is the I/O- and concurrency-heavy core, it runs most often, and the specs constrain its behavior most tightly. It follows `fsharp-profile-config` in a planned series of changes (see design.md, "Migration roadmap"), and it starts from the F# skeleton that was set up outside the series: `Vara.Domain`, `Vara.Engine`, a Native AOT `Vara.Cli` (`vara.exe`) and one test project per source project. The C# application stays the shipped product until the final cutover change.

## What Changes

- Grow the skeleton projects:
  - `src/fsharp/Vara.Domain`: the pure functional core, which gains the domain types and every backup decision. It does no I/O and has no infrastructure package references.
  - `src/fsharp/Vara.Engine`: the imperative shell, which gains file system scanning, the content-addressed store, the SQLite manifest, Win32 interop, the run lock, and composition of the backup run.
  - `src/fsharp/Vara.Cli`: gains `vara backup <profile>`, with interim plain-text output, so the engine runs inside the published Native AOT executable from this change onwards.
- Design the backup engine from the ground up with functional patterns, where the spike showed the value lies:
  - scanning in which failures travel with the entries, rather than in a side list that is only complete afterwards;
  - pure decisions for classification, deletion, move resolution and planning;
  - the run's state evolving as a pure function of completed operations;
  - effects such as manifest rows, checkpoints and progress decided by pure code and performed by a thin shell;
  - expected outcomes (a busy target, a refused mirror path) returned as typed results.
- Cover what the C# engine does beyond the slice the spike measured, among them:
  - migration of legacy `profile.db` collation;
  - the read-only empty manifest mode;
  - the dry-run plan summary;
  - read-only protection across moves and removals;
  - recovery from the hard-link limit;
  - the drive-root and UNC mirror path rules;
  - preserving the original failure when recording the failure itself fails.
- Replace C# test hooks (forced hard-link failure, forced capability) with effects passed into the shell, which tests can swap out.
- Verify against the specs with one F# test per backup-execution scenario that the engine owns. Keep a parity suite that runs the C# and F# engines on the same sources and requires identical manifests, mirrors and blobs, including failure paths.
- Add side-by-side benchmarks that compare C# with `Vara.Engine`, and keep the exploratory spike's recorded results as evidence.
- As its last task, update the following changes with this change's findings, and write the design and tasks of `fsharp-backup-presentation`.

The shipped `vara.exe` is still the C# build, so nothing changes for users: no CLI, output, on-disk format or configuration changes.

## Capabilities

### New Capabilities

None.

### Modified Capabilities

None. This is a behavior-preserving reimplementation that ships nothing yet (`skip_specs: true`). The existing `backup-execution` spec is the acceptance contract.

**Owned scenarios:** every `backup-execution` requirement except "Automatic system sleep is prevented during a real backup" and "Sleep-prevention request failure does not block backup", which are owned by `fsharp-backup-presentation`. The dry-run requirement is owned here through `vara backup --dry-run` and its interim summary; the dry-run display and JSON output come later, under `progress-reporting` and `cli-presentation`.

## Impact

- **Code:** `src/fsharp/Vara.Domain`, `src/fsharp/Vara.Engine` and `src/fsharp/Vara.Cli` grow. There are no changes to the C# projects.
- **Tests:** `tests/fsharp/Vara.Domain.Tests` gains pure and property-based tests; `tests/fsharp/Vara.Engine.Tests` gains spec-scenario tests and the C#/F# parity suite; `tests/fsharp/Vara.Cli.Tests` gains the `backup` command tests. The C# test suite is unchanged and must stay green.
- **Benchmarks:** `LanguageComparisonBenchmarks` is added to `benchmarks/Vara.Benchmarks`, comparing C# with `Vara.Engine`. The exploratory run `benchmarks/baselines/2026-11-08-csharp-vs-fsharp-slice` is committed as a recorded baseline.
- **Dependencies:** `Vara.Engine` gains Microsoft.Data.Sqlite.Core, SQLitePCLRaw.bundle_winsqlite3, System.IO.Hashing and Microsoft.Extensions.FileSystemGlobbing, all already pinned centrally. `FSharp.Core` moves to the .NET 11 GA version once it ships. This change depends on `fsharp-profile-config`, which provides the profile loading `vara backup` needs.
- **Process:** during the migration, C# behavior changes are frozen except for bug fixes, and each such fix gets a parity test so that both implementations stay aligned.
