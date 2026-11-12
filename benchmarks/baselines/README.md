# Benchmark baselines

Committed BenchmarkDotNet results used to detect performance regressions across large
changes. Each folder holds one run's `*-report-full.json` (used for comparison, including
every individual measurement), plus the GitHub Markdown and CSV reports for reading.

| Baseline | Code | Notes |
|---|---|---|
| [2026-11-12-fsharp-net11](./2026-11-12-fsharp-net11/) | commit `5f74bbb` (the F# build) | **Current reference baseline.** `--job short`, command below. Allocations are the asserted figures (budget 5%). `release-check.json` holds the CPU time, peak memory and executable size of the published `vara.exe` at the same commit (asserted by `Measure-ReleaseCheck.ps1`; wall-clock time is advisory). It was recorded with both the executable and the scratch directory in a Defender-excluded folder; the CPU interquartile ranges were 3.2%, 25% (a 31 ms scenario, at the timer's 15.6 ms resolution, so not meaningful) and 3.1%. The BenchmarkDotNet figures were recorded with Defender active, so trust their allocations and treat times as advisory |

The C# baselines and the earlier F# baselines were removed on 2026-11-12 (they remain in git history before this change). A C# against F# comparison on this machine, with the C# build at commit `826e890` and the F# build at `e60a4bf`, found F# allocations equal or lower except `BackupDifferBenchmarks.Diff` for `Added` and `Changed` (+27%). Commit `5f74bbb` fixed that and made the differ faster than C# in every case except `Deleted`, which sizes its set of seen paths from the manifest and so costs about 90 us (4096 tracked, nothing scanned) against 35 us before. Scan time is far lower in F# (the single-pass directory read). Single-threaded transfer (`TransferConcurrency=1`, which is also the pipeline default) is 1.2-1.7x slower in F# because the `durable-blob-store` commit (`bd4cda5`) flushes every blob to disk; before it, F# matched C#. That cost is accepted and is part of this baseline.

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

## Release check of the published executable

`benchmarks/Measure-ReleaseCheck.ps1` runs the published Native AOT `vara.exe` against fixtures and compares CPU
time, peak working set and executable size with `release-check.json` in a baseline folder. BenchmarkDotNet cannot
see these, and CPU time is measured only here. Publish first, then compare or record:

```powershell
dotnet publish src/Vara.Cli -c Release -r win-x64 -o artifacts/cli
Copy-Item artifacts/cli/vara.exe <scratch directory>/exe/      # the executable also belongs in the excluded folder
./benchmarks/Measure-ReleaseCheck.ps1 -Exe <scratch directory>/exe/vara.exe `
    -BaselineDirectory benchmarks/baselines/2026-11-12-fsharp-net11 `
    -ScratchDirectory <scratch directory>          # add -Record to write release-check.json instead of comparing
```

`-ScratchDirectory` is required and differs between machines. It must be a dedicated directory on a local drive and
excluded from Windows Defender scanning (real-time scanning makes the CPU time noisy); confirm the path before
recording, since the script cannot tell whether the folder is excluded. The executable should be run from an excluded
folder too: on 2026-11-12 the same build measured 3.7 s CPU (6.1% spread, 7.3 s wall) for the first scenario with the
scratch directory excluded but the executable outside it, and 3.1 s (4.0% spread, 4.1 s wall) with both inside. A scenario whose CPU time has an
interquartile range above 5% is reported as inconclusive, and then the check should be repeated on a quieter machine.

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
