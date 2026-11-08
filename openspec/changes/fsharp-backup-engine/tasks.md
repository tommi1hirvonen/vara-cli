## 1. Preparation

- [ ] 1.1 Confirm that `fsharp-profile-config` is complete: `vara` can resolve a profile by name from the YAML configuration. Verify that its tests pass in `dotnet test Vara.slnx`.
- [ ] 1.2 Move the SDK and the `FSharp.Core` pin in `Directory.Packages.props` to the .NET 11 GA release, or keep the RC pin and note it in the commit message if GA is not available yet. Verify with `dotnet --version` and with `dotnet build Vara.slnx`, which must succeed without FSharp.Core version warnings.
- [ ] 1.3 Commit the exploratory baseline `benchmarks/baselines/2026-11-08-csharp-vs-fsharp-slice` following `benchmarks/baselines/README.md`: delete the `*.html` reports and add a table row describing it as the faithful-port comparison. Verify that `./benchmarks/Compare-BenchmarkResults.ps1` comparing the folder with itself reports every case as OK.
- [ ] 1.4 Add Microsoft.Data.Sqlite.Core, SQLitePCLRaw.bundle_winsqlite3, System.IO.Hashing and Microsoft.Extensions.FileSystemGlobbing to `Vara.Engine`, and a project reference to the C# `Vara.Application` in `Vara.Engine.Tests` for the parity suite (`Vara.Infrastructure` and `Vara.Testing` are already referenced since `fsharp-profile-config`). Verify that `dotnet build Vara.slnx` succeeds and that `dotnet list src/fsharp/Vara.Domain package` still shows only FSharp.Core.

## 2. Pure domain core (`Vara.Domain`)

- [ ] 2.1 Design the domain model from the `backup-execution` spec and design D3: the types for scanned entries and scan failures, manifest rows and current state, pending changes, planned operations, completed operations and the run's state, plus the signature of every pure decision D3 lists. Use the profile types from `fsharp-profile-config`. Shape planned operations so that no consumer needs an impossible-case branch, and choose the run state's representation against the allocation budget (D6). Verify that `Vara.Domain` builds, that `git grep -n "invalidOp\|failwith" -- src/fsharp/Vara.Domain` returns nothing, and that the commit message records the allocations per file that the chosen shapes imply.
- [ ] 2.2 Add the pure scan rules: exclude and glob matching that accepts either separator, and mirror-path mapping for drive roots and UNC paths. Verify with tests in `Vara.Domain.Tests` for "Nested exclude written with the non-native path separator", "Drive-letter-root source is scanned from the actual drive root" and "UNC source path produces no mirror entries".
- [ ] 2.3 Add change classification and deletion detection (design D3). Verify with tests for the casing scenarios under "Incremental change detection" and the deletion-suppression scenarios under "Unreadable files do not abort the run", plus an FsCheck property: every tracked path ends up seen, deleted, or hidden by a failure.
- [ ] 2.4 Add move-candidate selection and move resolution as pure functions over the signatures the shell supplies (design D3). Verify with tests for the three scenarios of "Move detection avoids unbounded reads for non-matching candidates" and for tie-breaking in deleted-path order, plus FsCheck properties: no deleted row is consumed twice, and plan totals equal the sum of transfer sizes.
- [ ] 2.5 Add the evolution of the run's state: applying a completed operation, the manifest rows it produces, the checkpoint decision (with the clock passed in), the progress projection and the outcome. Verify with tests built from plain lists of completions, and an FsCheck property: any interleaving that keeps each file's own order yields the same counts, bytes and failed paths.
- [ ] 2.6 Add the run-level decisions: the snapshot summary, the cancel-or-complete choice, the dry-run plan summary, and the mapping of a run's result to an `Outcome` (failed paths or cancellation give `PartialFailure`). Verify with tests, including "No changes pending".

## 3. Imperative shell (`Vara.Engine`)

- [ ] 3.1 Add the kernel32 interop (`CreateHardLinkW`, and real-path resolution through `CreateFileW` and `GetFinalPathNameByHandleW` with stack and pooled buffers) and the prefix-replay and tee streams to `Vara.Engine`. Verify with tests for hard-link creation, its Win32 error path, real-path resolution of short and long paths, and byte-for-byte replay.
- [ ] 3.2 Build the content store on a `Links` effect record (design D4), covering mirror confinement via real-path resolution, read-only protection, and the hard-link-limit fallback. Verify with tests for every scenario under "Mirror writes are confined to the target root", "Hardlinked mirror entries are read-only", "Graceful degradation when a blob's hard-link limit is reached" and "Graceful degradation on filesystems without hardlink support". The failing link effect replaces the C# test hooks.
- [ ] 3.3 Build the manifest: schema, legacy `COLLATE NOCASE` migration, read-only empty mode, and batch checkpoints with WAL truncation. Verify with a test that migrates a legacy-schema database to the same rows as the C# repository does, and a test that opening a missing database read-only creates no file.
- [ ] 3.4 Build the scan shell over the pure rules, reporting failures alongside entries. Verify with tests for the "Symlinks and junctions are not followed" scenarios and the unreadable-file and unreadable-directory scenarios, using the same fixture techniques as the C# scanner tests.
- [ ] 3.5 Build signature reading: quick hashes in parallel, with a full hash only when a candidate is still possible. Verify that the file read count stays bounded for ruled-out candidates, for example by observing the read effect.
- [ ] 3.6 Build the execution shell: metadata operations, pooled transfers, and the single recorder (design D7), using or reshaping the skeleton's `Flow` combinators. Verify with tests for the "File modified during its own transfer is not recorded under stale metadata" scenarios, the checkpoint scenarios (using a short interval), a worker crash (an `AggregateException` after draining), and the token-driven "Graceful cancellation via Ctrl+C" scenarios.
- [ ] 3.7 Compose the backup run, which returns a refusal as a `Result` error (design D5), and the dry-run plan. Verify with tests for "Concurrent run prevention", "Original failure cause is preserved when failure recording itself fails", "Crash and interruption safety", and the engine-owned dry-run scenarios.

## 4. `vara backup` (`Vara.Cli`)

- [ ] 4.1 Add the `backup` command through `CommandAction.set`. It takes a positional profile argument, the legacy `--profile` option, `--config` and `--dry-run`, uses the token from the host's `GracefulCancellation`, and prints an interim plain-text summary styled by outcome. Verify with `Vara.Cli.Tests` for:
  - the three "Backup command accepts profile as a positional argument" scenarios;
  - the dry-run scenarios through `vara backup --dry-run`;
  - "Second Ctrl+C forces immediate termination";
  - the outcome-to-exit-code mapping of a refused run, a clean run, and a run with failed paths;
  - the rendering of the `fsharp-profile-config` errors: configuration missing, unknown profile, invalid profile.

## 5. Specification and parity coverage

- [ ] 5.1 Add one test per `backup-execution` scenario this change owns, named after the scenario title inside a module named after its requirement. Most come from groups 2 to 4; add the rest here. Add the coverage test for `openspec/specs/backup-execution/spec.md` using `Vara.Testing`, excluding the two sleep-prevention requirements. Verify that it passes.
- [ ] 5.2 Write the parity suite in `Vara.Engine.Tests`. It runs the C# and F# engines on the same sources and compares manifest rows, snapshots, mirror contents, read-only flags and blobs. It covers:
  - first and incremental backups (change, move, delete, add) at transfer concurrency 1 and 4;
  - scanner excludes, globs and an unavailable source;
  - the D8 failure paths: locked or unreadable entries, a torn read, no hard-link support, the hard-link limit, cancellation, a recorder crash, and a legacy-collation database;
  - cross-implementation continuation.

  Verify that `dotnet test tests/fsharp/Vara.Engine.Tests` passes.

## 6. Native AOT, benchmarks and cleanup

- [ ] 6.1 Publish `src/fsharp/Vara.Cli` with Native AOT (`dotnet publish -c Release -r win-x64 -o artifacts/fs-cli`). Verify that there are no IL2xxx/IL3xxx warnings, and that the published `vara.exe` completes a first and an incremental `backup` of a scratch profile with exit code 0, producing a target that the C# `vara.exe` then continues without re-transfers.
- [ ] 6.2 Add `LanguageComparisonBenchmarks` to `benchmarks/Vara.Benchmarks`. It has scan, executor and pipeline classes, with the same fixtures and parameters as the existing C# benchmarks, C# as the baseline, and `Vara.Engine` as the comparison. Run it with `--job short --exporters fulljson`. Verify that every case allocates at most 5% more than C# (design D6), and record the allocation ratios in the commit message.
- [ ] 6.3 Convention review against the proposal's "Functional steering". Verify that:
  - `git grep -nE "\binterface\b|\binherit\b|\babstract\b|\bmember\b|\bnew\s*\(|\b(Helper|Manager|Service|Util)\b|\bI[A-Z][a-z]" -- src/fsharp/Vara.Domain` returns nothing;
  - the same search in `src/fsharp/Vara.Engine` and `src/fsharp/Vara.Cli` has only hits that the commit message justifies (interop handles, `IDisposable`, host callbacks);
  - `git grep -nw mutable -- src/fsharp` lists only locals inside one function, each with its measured reason in the commit message, and none in `Vara.Domain`'s decision functions;
  - the backup run reads as a pipeline from scan to outcome, with its effects assembled in the composition root, and no type holds the engine's dependencies as fields.
- [ ] 6.4 Final verification:
  - `dotnet build Vara.slnx` reports no FS or CS warnings (NU1903 for the preview ILCompiler is out of scope);
  - `dotnet test Vara.slnx` passes for both the C# and F# suites;
  - the commit message records non-comment line counts and `mutable`/`<-` counts for the engine, compared with the C# backup path.

## 7. Update the following changes

- [ ] 7.1 Using the `openspec-update-change` workflow, write the design and tasks of `fsharp-backup-presentation` from this change's findings. Its tasks end with the same "update the following changes" step. Settle the Gate B measurement method and the memory and executable-size margins in its design. Keep the "Functional steering" section in each later proposal, extended with the conventions this change established, and make sure that every later change's tasks contain a convention review task, like 6.3. Revise the proposals of the later changes only if their scope, dependencies or gates have shifted, and confirm that every scenario in `openspec/specs` is still owned by exactly one change in the series. Verify that `openspec validate` passes for every changed change.
