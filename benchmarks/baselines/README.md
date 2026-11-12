# Benchmark baselines

Committed BenchmarkDotNet results used to detect performance regressions across large
changes. Each folder holds one run's `*-report-full.json` (used for comparison, including
every individual measurement), plus the GitHub Markdown and CSV reports for reading.

| Baseline | Code | Notes |
|---|---|---|
| [2026-11-12-fsharp-net11](./2026-11-12-fsharp-net11/) | commit `e60a4bf` (the F# build) | **Current reference baseline.** `--job short`, command below. Allocations are the asserted figures (budget 5%). `release-check.json` is carried over from the earlier 2026-11-09 F# baseline (CPU time, peak memory and executable size of the published `vara.exe`, asserted by `Measure-GateB.ps1`). It predates the durable-blob-store and later commits, and its scenario 1 was recorded with an interquartile range of 14.6% on a machine with Defender active, so re-record it with `Measure-GateB.ps1 -Record` on a quiet machine before relying on it. The BenchmarkDotNet figures were also recorded with Defender active |

The C# baselines and the earlier F# baselines were removed on 2026-11-12 (they remain in git history before this change). A C# against F# comparison on this machine, with the C# build at commit `826e890` and the F# build at `e60a4bf`, found F# allocations equal or lower except `BackupDifferBenchmarks.Diff` for `Added` and `Changed` (+27%; `Deleted` is -58%). Scan time is far lower in F# (the single-pass directory read). Single-threaded transfer (`TransferConcurrency=1`, which is also the pipeline default) is 1.2-1.7x slower in F# because the `durable-blob-store` commit (`bd4cda5`) flushes every blob to disk; before it, F# matched C#. That cost is accepted and is part of this baseline.

Environment of the `2026-11-12-fsharp-net11` run: Windows 11 25H2 (10.0.26200), AMD Ryzen AI
9 HX PRO 375, .NET SDK 11.0.100, BenchmarkDotNet 0.16.0-preview.2, fixtures
in the system drive's temp directory, Windows Defender active. Command:

```powershell
dotnet run -c Release --project benchmarks/Vara.Benchmarks -- --filter '*' --job short --exporters fulljson github csv --artifacts benchmarks/baselines/2026-11-12-fsharp-net11
```


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
    -Baseline benchmarks/baselines/2026-11-12-fsharp-net11 `
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
