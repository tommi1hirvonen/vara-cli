## Context

See proposal.md for why. This design is written after the last migration change, `fsharp-profile-management`, so it describes what the series left behind and not what it planned.

What exists:

- **F# projects** under `src/fsharp` (`Vara.Domain`, `Vara.Engine`, `Vara.Cli`) and `tests/fsharp` (`Vara.Domain.Tests`, `Vara.Engine.Tests`, `Vara.Cli.Tests`, `Vara.Testing`), plus `benchmarks/Vara.Benchmarks.FSharp` (an adapter). Every command of the C# build has an F# implementation.
- **C# projects** under `src` (`Vara.Core`, `Vara.Application`, `Vara.Infrastructure`, `Vara.Cli`), their test projects (`Vara.Core.Tests`, `Vara.Application.Tests`, `Vara.Infrastructure.Tests`, `Vara.Cli.Tests`, `Vara.IntegrationTests`, `Vara.TestSupport`) and `benchmarks/Vara.Benchmarks`.
- **Tests that reach the C# build.** In `Vara.Engine.Tests`, which references `Vara.Application` and `Vara.Infrastructure`: `ParityTests` (the YAML corpus through both loaders), `EngineParityTests`, `RetentionParityTests`, `ProfileFileParityTests` (both writers, both loaders), and a use in `SqliteManifestTests`. In `Vara.Cli.Tests`, which references the C# `Vara.Cli` with `ReferenceOutputAssembly="false"` and runs it as a process: `CommandParityTests`, `HistoryParityTests`, `MaintenanceParityTests`. `ProfileWriterTests` and `ProfileFileParityTests` also use the F# test helpers `ParityTests.describeProfile` and `describeCSharpProfile`.
- **Spec coverage.** `SpecCoverageTests` in `Vara.Engine.Tests` checks the specs from `profile-config` to `profile-management`. It leaves out the `cli-presentation` requirements that other changes own, and it does not check `portable-distribution`.
- **Layout files.** `Vara.slnx` lists both sets of projects in two groups. The repository root has `Directory.Build.props` (C#: `net11.0`, `Nullable`, `LangVersion preview`, `CS8509` as an error) and `Directory.Packages.props`. `src/fsharp`, `tests/fsharp` and `benchmarks/Vara.Benchmarks.FSharp` each have their own `Directory.Build.props`.
- **Benchmarks.** `benchmarks/Vara.Benchmarks` holds the C# scenarios (`DirectoryScan`, `BackupStage`, `BackupTransfer`, `ContentStoreBuffer`, `StreamingSignature`) and `LanguageComparisonBenchmarks`. `Measure-GateB.ps1` measures two Native AOT builds against each other, and `Compare-BenchmarkResults.ps1` compares result files. The reference results are in `benchmarks/baselines`.

Gaps that the series recorded and that this change must not lose:

- The retention-pruning and backup-integrity specs say a cancelled prune or check "exits successfully", and both builds exit with the partial-failure code. The tests assert the C# code with a comment.
- `vara profiles` exits with success on Ctrl+C, as the C# build does. The editor's menus use the default page size of the F# `Prompts.Select`, not the C# build's 15 rows.
- The F# editor edits retention and concurrency on sub-screens, with live validation, where the C# build asks for the four counts one after another. The F# restore sets a file's modification time and the C# restore does not.
- An existing SQLite test and a `CheckCommandTests` test fail now and then under parallel load and pass when rerun alone.
- Whether Spectre's text prompt runs under Native AOT in a real terminal was not exercised by the profile-management change (it publishes without warnings, and the C# build ships the same prompt).

## Goals / Non-Goals

**Goals:**

- After the change, one implementation builds, tests and ships, and nothing in the repository refers to a C# project, an adapter or a comparison between two builds.
- Every check that the parity suite made keeps a counterpart that needs no C# build: a recorded corpus or a scenario test.
- The published `vara.exe` is the F# Native AOT build, and the `portable-distribution` scenarios pass on it.
- The history shows the move: files are moved with `git mv`, so that `git log --follow` works.

**Non-Goals:**

- Changing behavior to match a spec where the two builds agree with each other (the exit code of a cancelled maintenance run). That is an ordinary change with its own spec edit.
- Keeping the ability to run the two builds against each other after the cutover.
- Reaching a wall-clock target. Timing on this machine is noisy; allocations and CPU time are the baseline.

## Decisions

### D1. The audit runs first, and the C# code stays until the corpus is recorded

The convention review is a whole-codebase audit against the rules in the proposal, and it runs before anything is removed, because a finding may need the C# build to decide what correct behavior is. Mechanical findings (a stray `let mutable`, a wildcard on a union the code owns) are fixed in the same task. Findings that need design work are recorded as notes in this change's tasks and become separate changes. One known wildcard is the last case of `ProfileEditor.update`, which ignores an input for a screen that is not showing; the audit records it as accepted.

Alternative: audit after the removal. Rejected: the C# code is the only oracle for a behavior question.

### D2. The parity suite becomes a recorded corpus, plain data and one comparison function

For each parity test, the expected result is recorded once from the C# build, stored as a file, and compared with the F# result. The corpus is data (text and JSON files in a `Corpus` folder of the test project) and one function per kind of comparison. There is no harness class and no abstraction for running two builds.

- **Loader corpus.** The YAML cases of `ParityTests` (over 60) with the C# result for each (the profiles' description lines, or the error message). The test loads each case with the F# loader and compares.
- **Retention boundaries.** The generated timestamp cases of `RetentionParityTests` with the C# decision, as sets of ids.
- **Writer files.** The files that the C# writer produces for the cases of `ProfileFileParityTests`, as golden YAML. The F# loader must read each as the recorded profiles, and the F# writer's output must load in the F# loader to the same profiles. The direction "F# file loaded by the C# loader" has no counterpart, because the loader corpus already fixes how the C# loader reads files; the writer's output is checked by the round trip through `render`, `parse` and `validateProfiles`.
- **Process outputs.** The normalized JSON, exit codes, projected rows and counts of `CommandParityTests`, `HistoryParityTests` and `MaintenanceParityTests`, recorded from the C# build on the same fixtures.

A small recorder (a script under `tests`, not a project) produces the corpus from the C# build while it still exists, and is removed with it. The recorded files are reviewed once as a diff against a fresh run of the F# build: every difference is either a bug (fixed first) or a known difference from the list in Context (recorded in the file with a reason).

Alternatives: delete the parity suite (rejected: the loader corpus and the retention boundaries have no scenario test that is as wide); keep the C# build alive for tests (rejected: it is the cost this change ends).

### D3. The move is mechanical and in a fixed order

1. Remove the C# source projects, their tests and the C# benchmark project from `Vara.slnx`, then delete them.
2. Move `src/fsharp/*` to `src/` and `tests/fsharp/*` to `tests/` with `git mv`, then fix `ProjectReference` paths, `Vara.slnx`, `Directory.Build.props` and the scripts.
3. Merge the property files: the root `Directory.Build.props` keeps `net11.0` and the F#-relevant settings; the C#-only settings (`Nullable`, `LangVersion preview`, `CS8509`) are dropped unless an F# project needs them; the per-folder files are folded into it or removed when they become the same. `Directory.Packages.props` loses the packages that only C# projects used; each removal is checked by a restore and a build.

Both builds name their assembly `vara`, which is why the old test project references the C# one with `ReferenceOutputAssembly="false"`. That reference disappears with the C# project, and so does the process-parity pattern.

### D4. Benchmarks are ported to F#, and the comparison is removed

The scenarios of the C# benchmark project that measure the engine (scan, stage, transfer, content-store buffer, signature) are rewritten in an F# benchmark project over `Vara.Engine`, named `Vara.Benchmarks`, so that their names in the baseline stay the same. `LanguageComparisonBenchmarks` and `Vara.Benchmarks.FSharp` are removed. A new baseline is recorded under `benchmarks/baselines` (the allocations, and the timings with the noise caveat), and `baselines/README.md` says that the older folders compare the C# build with the F# build and are kept as history.

`Measure-GateB.ps1` stays as an optional release check, rewritten to compare the published F# `vara.exe` with the recorded baseline for CPU time, memory and executable size (which can be asserted) and to report wall-clock time as advisory. `Compare-BenchmarkResults.ps1` stays if it compares result files of one build over time, and is removed if it only compares the two builds. Its paths follow the new layout.

### D5. The distribution is checked on the executable alone

The `portable-distribution` scenarios are verified by publishing with Native AOT to `artifacts`, copying `vara.exe` alone into an empty directory, and running a backup, a snapshot listing and a restore there. The first requirement (no companion native SQLite library) is checked by the directory holding one file, and the run succeeding. The second (a documented minimum OS) is a README statement, and a test that reads the README for the stated version, so that the claim and the check stay together. A spec-coverage test for `portable-distribution` is added next to the others, and the exclusion list of the `cli-presentation` check is removed, so that every scenario of every spec is covered with no exceptions.

### D6. Where the series' conventions go

The "Functional steering" section is kept in this proposal, extended with what `fsharp-profile-management` established. After the cutover there is no later change to carry it, so one task copies the rules that still apply to the code into the repository's contributor documentation (the README's development section), and the design notes of the archived changes stay in the archive.

## Risks / Trade-offs

- [The recorded corpus is wrong, because the C# build had a bug that the F# build shares or fixed] → The corpus is recorded once and diffed against a fresh F# run before the C# code is removed; each difference is classified in the file.
- [A path or reference breaks silently in the move] → Each step ends with a full restore, build and test run; `git grep` for the old paths (`src/fsharp`, `tests/fsharp`, `Vara.Infrastructure`) must be empty outside the archive and the baselines.
- [The Native AOT publish differs from the debug build] → The `portable-distribution` check and a run of each command from the copied `vara.exe` are part of the verification, not only the in-process tests.
- [Removing packages breaks a transitive dependency] → Packages are removed one group at a time, each with a restore and a build.
- [The benchmark port changes what a scenario measures] → The first F# run is compared with the last C# baseline for the same scenario; a difference beyond the allocation budget is investigated before the new baseline is recorded.
- [An F#-only mistake appears once the C# oracle is gone] → The scenario tests and the recorded corpus remain, and the known gaps in Context are written into the tests as comments with their reason.

## Migration Plan

The change lands as a series of commits in the order of the task groups, each of which builds and passes its tests, so that any of them can be reverted alone. The C# code stays in the history. The shipped artifact changes from the C# `vara.exe` to the F# one in the commit of the distribution group; to roll back, revert that commit and the removal commits, which restores the C# projects and the layout.

## Open Questions

- Whether `Compare-BenchmarkResults.ps1` still has a use once the two builds are gone. The task that rewrites the scripts decides, and it does not change the other tasks.
