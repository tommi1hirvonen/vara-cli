# Benchmark baselines

Committed BenchmarkDotNet results used to detect performance regressions across large
changes. Each folder holds one run's `*-report-full.json` (used for comparison, including
every individual measurement), plus the GitHub Markdown and CSV reports for reading.

| Baseline | Code | Notes |
|---|---|---|
| [2026-11-09-fsharp-net11](./2026-11-09-fsharp-net11/) | the working tree of `fsharp-cutover` on top of commit `62f996e` (the F# build, after the C# projects were removed), machine below | **Current reference baseline.** `--job short`, command below. Allocations are the asserted figures (budget 5%). `release-check.json` holds the CPU time, peak memory and executable size of the published `vara.exe` (asserted by `Measure-GateB.ps1`) and its wall-clock time (advisory); `release-check.md` is a verification run against it. Scenario 1's CPU figures were recorded with an interquartile range of 14.6% on a machine with Defender active, so re-record them with `Measure-GateB.ps1 -Record` on a quiet machine before relying on them |
| [2026-11-06-csharp-net11](./2026-11-06-csharp-net11/) | commit `3692197` (the C# build) | History: the last C# reference. The scenario names are the same as in the current baseline, so `Compare-BenchmarkResults.ps1` compares the two |
| [2026-11-08-csharp-async-net11](./2026-11-08-csharp-async-net11/) | the C# build after `csharp-async-backup-recording` | History: C# against C#, `--job short` |
| [2026-11-08-csharp-vs-fsharp-slice](./2026-11-08-csharp-vs-fsharp-slice/) | exploratory F# spike (code not kept) | History: compares the C# and F# builds, as evidence behind the migration. Not a regression baseline |
| [2026-11-08-csharp-vs-fsharp-engine](./2026-11-08-csharp-vs-fsharp-engine/) | after `fsharp-backup-engine` | History: compares the C# and F# builds (`LanguageComparisonBenchmarks`, since removed). Not a regression baseline |
| [2026-11-08-gate-b](./2026-11-08-gate-b/) | after `fsharp-backup-presentation` | History: the migration's Gate B, native C# `vara.exe` against F#. Not a regression baseline |

The folders dated before 2026-11-09 compare the two builds, or record the C# build; they are kept as history, and nothing in the repository runs the C# build any more.

Allocation comparison of the first F# run with the last C# baseline (`Compare-BenchmarkResults.ps1 -AllocationTolerancePercent 5`): every case is within 5% except `BackupDifferBenchmarks.Diff` for the `Deleted` scenario (+80%) and, diluted by the other kinds, `Mixed` (+17%). The F# `DiffResult.Deleted` carries each deleted path with its tracked state (a tuple per entry) so that move planning needs no second lookup, where the C# differ returned bare paths; the difference is about 80 bytes per deleted entry and is confined to the diff stage (the whole pipeline is within 1%). Time ratios are advisory: the runtimes differ (RC1 against RC3), and the machine is noisy.

Environment of the `2026-11-09-fsharp-net11` run: Windows 11 25H2 (10.0.26200), AMD Ryzen AI
9 HX PRO 375, .NET SDK 11.0.100-rc.3.26522.148, BenchmarkDotNet 0.16.0-preview.2, fixtures
in the system drive's temp directory, Windows Defender active. Command:

```powershell
dotnet run -c Release --project benchmarks/Vara.Benchmarks -- --filter '*' --job short --exporters fulljson github csv --artifacts benchmarks/baselines/2026-11-09-fsharp-net11
```

Environment of the `2026-11-06-csharp-net11` run (history): Windows 11 25H2, the same CPU, .NET SDK 11.0.100-rc.1.26425.128, BenchmarkDotNet 0.16.0-preview.2.

## Recording a new run

1. On the same machine and storage, with as little background load as possible, run the
   same command with a new folder name (for example `2027-01-15-fsharp-net11`).
2. Before committing the folder, delete the redundant `*.html` reports. The `*.log` file
   is gitignored.
3. Add a row to the table above. If the run becomes the new reference, move the
   "Current reference baseline" marker to it.

## Comparing a run

```powershell
./benchmarks/Compare-BenchmarkResults.ps1 `
    -Baseline benchmarks/baselines/2026-11-09-fsharp-net11 `
    -Candidate benchmarks/baselines/2027-01-15-fsharp-net11
```

The script matches cases by full name and parameters and compares **median** time and
**allocated bytes per operation**. Its default tolerances:

| Category | Tolerance | Why |
|---|---|---|
| In-memory CPU time (`BackupDifferBenchmarks`, most of `StreamingSignatureBenchmarks`) | +10% | Stable across `--job short` runs |
| I/O-bound time (executor, pipeline, planner, content store, directory scan, `*FromFile`) | +30% | Filesystem, cache and SQLite noise; several cases have a 99.9% CI wider than their mean at 3 iterations |
| Allocations | +10% and more than 256 B | Nearly deterministic, so the most reliable regression signal |

The script exits with code 1 when any case regresses or is missing. Override the
tolerances with `-CpuTimeTolerancePercent`, `-IoTimeTolerancePercent`,
`-AllocationTolerancePercent`, and `-AllocationToleranceBytes`.

## Reading the results

- **Allocations are the reliable signal.** Treat an allocation regression as real until
  it is explained.
- **Time at `--job short` is advisory.** Three iterations catch large regressions but not
  small ones, and time depends heavily on machine state: re-running the same commit has
  shown differences of up to 2x on I/O-bound cases and over +100% on sub-microsecond cases.
- **Small multi-threaded file-transfer cases are bimodal.** For example
  `BackupExecutorBenchmarks.ExecuteTransfers(TransferConcurrency: 4, FileCount: 8, ...)`
  has varied between about 25 ms and 55 ms on the same commit.
- A flagged time case is a prompt to re-run that class with the default job, both on the
  baseline commit and on the candidate, in the same session (for example
  `--filter '*BackupExecutorBenchmarks*'` without `--job short`), before drawing
  conclusions.

If the runtime or SDK version changes between runs, the script warns. Re-run the baseline
commit on the new runtime first so that the comparison isolates the code change.
