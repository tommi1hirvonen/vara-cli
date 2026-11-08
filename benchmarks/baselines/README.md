# Benchmark baselines

Committed BenchmarkDotNet results used to detect performance regressions across large
changes. Each folder holds one run's `*-report-full.json` (used for comparison, including
every individual measurement), plus the GitHub Markdown and CSV reports for reading.

| Baseline | Code | Notes |
|---|---|---|
| [2026-11-06-csharp-net11](./2026-11-06-csharp-net11/) | commit `3692197` (after the channel-based progress and recording refactors) | **Current reference baseline.** `--job short`, command below |
| [2026-11-08-csharp-async-net11](./2026-11-08-csharp-async-net11/) | after `csharp-async-backup-recording` (on top of `csharp-closed-set-redesigns`) | `--job short`. Allocations equal or lower in every case (executor and pipeline within about -0.5%). Time flags against the reference (pipeline +67-80%, `ExecuteTransfers(1, 8, 1 MiB)` +54%, and one untouched sub-microsecond case) were machine drift: a same-session default-job re-run of `BackupPipelineBenchmarks` and `BackupExecutorBenchmarks` on the base commit and on this change gave equivalent times (for example pipeline 64 files x 1 MiB 278 ms vs 278 ms, executor concurrency 1/4 within noise) |
| [2026-11-08-csharp-vs-fsharp-slice](./2026-11-08-csharp-vs-fsharp-slice/) | exploratory F# spike (code not kept), `--job short` | **Not a regression baseline.** Faithful-port comparison of C# and F# on the scan and backup slice (`ScanLanguageComparison`, `ExecutorLanguageComparison`, `PipelineLanguageComparison`), kept as the evidence behind the F# migration series. Reports are under `results/` |
| [2026-11-08-csharp-vs-fsharp-engine](./2026-11-08-csharp-vs-fsharp-engine/) | after `fsharp-backup-engine`, `--job short` | **Not a regression baseline.** C# engine against the F# `Vara.Engine` (`LanguageComparisonBenchmarks`: scan, executor, pipeline), same fixtures and parameters as the C# benchmarks. Allocation ratio 0.98 to 1.01 in every case (budget 1.05). Time is advisory: the short job is noisy on this machine (F# ratios from 0.29 to 1.86, with no consistent direction) |

Environment of the `2026-11-06-csharp-net11` run: Windows 11 25H2 (10.0.26200), AMD Ryzen AI
9 HX PRO 375 (12 cores / 24 threads), 64 GB RAM, .NET SDK 11.0.100-rc.1.26425.128, .NET
runtime 11.0.0-rc.1, BenchmarkDotNet 0.16.0-preview.2, fixtures in the system drive's temp
directory. Command:

```powershell
dotnet run -c Release --project benchmarks/Vara.Benchmarks -- --filter '*' --job short --exporters fulljson --artifacts benchmarks/baselines/2026-11-06-csharp-net11
```

## Recording a new run

1. On the same machine and storage, with as little background load as possible, run the
   same command with a new folder name (for example `2027-01-15-csharp-net11`).
2. Before committing the folder, delete the redundant `*.html` reports. The `*.log` file
   is gitignored.
3. Add a row to the table above. If the run becomes the new reference, move the
   "Current reference baseline" marker to it.

## Comparing a run

```powershell
./benchmarks/Compare-BenchmarkResults.ps1 `
    -Baseline benchmarks/baselines/2026-11-06-csharp-net11 `
    -Candidate benchmarks/baselines/2027-01-15-csharp-net11
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
