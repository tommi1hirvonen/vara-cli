# Benchmark baselines

Committed BenchmarkDotNet results used to detect performance regressions across large
changes. Each folder holds one run's `*-report-full.json` (used for comparison, including
every individual measurement), plus the GitHub Markdown and CSV reports for reading.

| Baseline | Code | Notes |
|---|---|---|
| [2026-11-12-fsharp-net11](./2026-11-12-fsharp-net11/) | commit `5f74bbb` | **Current reference baseline.** `--job short`, command below. Allocations are the asserted figures (budget 5%). `release-check.json` holds the CPU time, peak memory and executable size of the published `vara.exe` (re-recorded on 2026-11-13 with the interactive and backup change tree changes applied, scratch directory and executable in a Defender-excluded folder; the earlier file was recorded at the same commit as the BenchmarkDotNet figures) (asserted by `Measure-ReleaseCheck.ps1`; wall-clock time is advisory). It was recorded with both the executable and the scratch directory in a Defender-excluded folder; the CPU interquartile ranges were 3.2%, 25% (a 31 ms scenario, at the timer's 15.6 ms resolution, so not meaningful) and 3.1%. The BenchmarkDotNet figures were recorded with Defender active, so trust their allocations and treat times as advisory |
| [2026-11-13-fsharp-net11](./2026-11-13-fsharp-net11/) | working tree with the interactive and backup change tree changes (`interactive-change-tree`, `plan-change-tree`), uncommitted when recorded | Release check only (`release-check.json`); no BenchmarkDotNet reports, so the 2026-11-12 baseline remains the reference for those. Recorded with the scratch directory and `vara.exe` in `vara-cli-scratch` (CPU interquartile ranges 1.2%, 0% and 0.5%). Against the 2026-11-12 release check: CPU 1.05x and 0.97x on the two asserted scenarios, peak working set 0.997x, executable 1.019x. Use it as the release-check baseline for later changes (`-BaselineDirectory benchmarks/baselines/2026-11-13-fsharp-net11`). |
| [2026-11-13-tree-browser-fsharp-net11](./2026-11-13-tree-browser-fsharp-net11/) | `interactive-tree-files` (the indexed browser with cached rows) | Baseline for `TreeBrowserBenchmarks` only (`--job short`, run with the artifacts in `vara-cli-scratch`), a class the 2026-11-12 reference does not have; compare a run of that class against this folder. 200,000 entries over about 2,000 directories: 20 cursor steps with frames 60 us and 500 KB, Expand All with a frame 100 ms and 63 MB, opening (index build) 95 ms and 49 MB. Allocations are the asserted figures. The figures of the browser before this change (4,763 ms and 3,265 MB for the 20 steps) are in the archived change's `design.md`. |

Earlier baselines were removed on 2026-11-12; they remain in git history. Two known costs are part of the reference baseline and are not regressions to chase:

- `BackupDifferBenchmarks.Diff` for `Deleted` sizes its set of seen paths from the manifest, so it costs about 90 us with 4096 tracked paths and nothing scanned.
- Single-threaded transfer (`TransferConcurrency=1`, which is also the pipeline default) is 1.2-1.7x slower than it was before the `durable-blob-store` commit (`bd4cda5`), because every blob is flushed to disk before it takes its content name. That durability is deliberate.

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
