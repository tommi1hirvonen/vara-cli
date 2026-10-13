# Vara benchmarks

The benchmark executable uses BenchmarkDotNet to measure production code without
including fixture creation or reset work in the timed methods. Build and run it in
Release mode:

```powershell
dotnet run -c Release --project benchmarks/Vara.Benchmarks -- --list flat
dotnet run -c Release --project benchmarks/Vara.Benchmarks -- --filter '*BackupDifferBenchmarks*'
dotnet run -c Release --project benchmarks/Vara.Benchmarks -- --filter '*BackupPlannerBenchmarks*'
dotnet run -c Release --project benchmarks/Vara.Benchmarks -- --filter '*DirectoryScanBenchmarks*'
dotnet run -c Release --project benchmarks/Vara.Benchmarks -- --filter '*ContentStoreBufferBenchmarks*'
dotnet run -c Release --project benchmarks/Vara.Benchmarks -- --filter '*BackupExecutorBenchmarks*'
dotnet run -c Release --project benchmarks/Vara.Benchmarks -- --filter '*BackupPipelineBenchmarks*'
```

The project pins BenchmarkDotNet `0.16.0-preview.2`, which supports this solution's
.NET 11 target for the generated out-of-process toolchain. BenchmarkDotNet's default
jobs therefore run in separate processes and can be used for local before/after
comparisons. Add `--job Dry` for a fast smoke test only; its single short iteration is
not suitable for performance comparisons. Do not add `--inProcess` when collecting
baseline results.

## Coverage and interpretation

- `StreamingSignatureBenchmarks` isolates prefix replay and measures quick-hash-only
  and quick-plus-full-hash flows using deterministic in-memory content and the real
  `XxHash128Hasher`.
- `BackupDifferBenchmarks` covers unchanged, added, changed, deleted, and mixed
  file-state distributions with prepared inputs.
- `BackupPlannerBenchmarks` measures the planner's real file reads and hashing for a
  same-size move candidate, both when the contents match and when the quick hash rules
  out a match. Its I/O is part of the measured operation.
- `ContentStoreBufferBenchmarks` measures the existing buffered source-to-temp-file
  tee-and-hash path across buffer sizes, including the current 1 MiB setting. It uses
  prepared source files and measures the real file read, write, and hash work.
- `DirectoryScanBenchmarks` scans and fully enumerates deterministic temporary trees;
  scan enumeration is lazy, so consuming `Entries` is required for a complete timing.
- `BackupExecutorBenchmarks` measures source reads, hashing, content-store writes,
  mirror placement, and manifest writes for a prepared all-added plan.
- `BackupPipelineBenchmarks` measures a first backup of an all-added source tree using
  the real scanner, planner, executor, content store, run lock, and SQLite manifest.

Input sizes and file counts are BenchmarkDotNet parameters. Temporary source fixtures
are created once per benchmark case. Target and manifest state for executor and pipeline
benchmarks are reset during iteration setup, outside the timed method; those benchmarks
therefore represent repeated clean first runs, not incremental no-op runs.

The storage-backed scenarios are intended for a local Windows filesystem. Disk,
filesystem-cache, SQLite, and hardlink behavior affect their results, so compare them
on the same machine and storage configuration, avoid concurrent benchmark runs, and
record the benchmark command, runtime, OS, and storage location when keeping a
before/after comparison. Run component-level in-memory benchmarks separately from
storage-backed benchmarks when investigating CPU or allocation changes.

BenchmarkDotNet writes reports under `BenchmarkDotNet.Artifacts/`, which is ignored by
git. Results are for local investigation and are not enforced as CI regression
thresholds.
