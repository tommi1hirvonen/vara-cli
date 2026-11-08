## Context

See proposal.md for why. This design builds on `fsharp-backup-engine`, which is applied before it.

What that change left in place, and what this design uses:

- `Vara.Engine.BackupRun.run` and `dryRun` take a `BackupEnv` record. Its `Observers` field is the only way the engine reports progress: `TransferPhaseStarting`, `BytesCopied` (a `voption`, so chunk events cost nothing unless a display wants them), `FileStarted`, `FileFinished` and `FileTransferred`. Every observer is called on the single recorder, one call at a time.
- `Vara.Domain.Run` holds the run state, a `Progress` struct (bytes and files against the plan's totals), `Run.finish`, `Run.summarizePlan`, `Run.outcome` and `PlanSummary`. `BackupSummary.fs` in `Vara.Cli` is an interim plain-text summary that this change replaces.
- `Vara.Cli` has the `backup` command (`BackupCommand.create`), the `CommandAction.set` template, `GracefulCancellation`, the outcome styles and `ErrorReporting`. `Program.fs` is the composition root.
- `Vara.Engine.Tests` has the parity suite (reflection and decorators over the C# engine, no C# source touched) and a spec-coverage test that matches test names to scenario titles. `Vara.Cli.Tests` uses `Spectre.Console.Testing`.
- `benchmarks/Vara.Benchmarks` has `LanguageComparisonBenchmarks` (scan, executor, pipeline), and `benchmarks/Vara.Benchmarks.FSharp` is the adapter that builds the F# side's fixtures.

The C# presentation code is the behavioral reference, not a design: `BackupProgressColumn`, `BackupProgressRendering`, `LiveProgress`, `WindowsTerminalProgress`, the outcome reporters and `WindowsBackupPowerRequest`.

## Goals / Non-Goals

**Goals:**

- Every owned scenario is verified by a test named after it, with the display decisions tested on plain data before any rendering.
- Output parity with C#, mechanically checked where the output is machine-readable (JSON) and by `Display` assertions where it is not.
- Settle the Gate B measurement method and the memory and executable-size margins here, so that the measurement itself is mechanical when the change is complete.

**Non-Goals:**

- Presentation for commands other than `backup`. The shared pieces (outcome styles, output-mode choice, the Windows Terminal indicator) are built so that later changes reuse them, but nothing else is migrated here.
- Restore and prune progress, which belong to later changes.
- Any change to the progress spec. A behavior difference found while porting is recorded as a bug fix to C# first, with a parity test, not absorbed here.

## Decisions

### D1. The display is a pure function of a tracker and a clock value

Observer calls feed a `Tracker`, an immutable record: the highest byte count seen (the never-regressing rule is `max`), files done, the in-flight paths, the phase, the time of the last admitted redraw, and the time the transfer phase began. `Tracker.apply` takes the event and a clock value and returns a new tracker. `Display.view` takes a width, a clock value and a tracker and returns a `Display`: plain data holding lines, fields with fixed widths, and style roles. Throughput and ETA are functions of the tracker and the clock value, so a heartbeat during a stall is just another call to `view` with a later clock value.

The tracker lives in `Vara.Domain` (the types, `apply`, `view`, throughput and ETA), with no reference to Spectre.Console or the clock. The one cell that holds the current tracker value is created inside `withProgress`, the bracket that runs the backup, and the observer closures update it. This is the host-callback boundary the engine already defines (observers run one at a time on the recorder), so the cell is local to one function and is not shared state.

Alternatives considered:

- **A progress-reporter class with fields:** rejected by the series rules, and it would have repeated the C# design (`BackupProgressState` plus a column class).
- **Pushing `RunState` through the observers:** rejected. The recorder's state changes per file and would be allocated again for the display. The observers carry the few values a display needs, and the display adds its own state.

### D2. Rate limiting is a pure admission test

`Throttle.admit : interval -> lastAdmitted -> now -> bool` decides whether numeric fields are redrawn. The first transfer update and the final update are always admitted (the tracker marks them), and file lifecycle events change only the active list. Coalescing the active-list refresh needs no queue: the renderer redraws on a timer tick and on a lifecycle event with the latest tracker value, so a backlog cannot form. A path that finished before a refresh is captured is not in the tracker's in-flight set, which gives the "completed file is not shown as active" scenarios by construction.

### D3. Spectre.Console is touched in one module

`LiveRenderer` is the only module that references `Spectre.Console` progress or live-display types. It interprets a `Display` for the interactive terminal. `PlainRenderer` interprets the same value for redirected output, `NO_COLOR` and terminals without color support, and the final summary uses the same `Display` pipeline. `OutputMode.choose` is a pure function of the console's capabilities and the `--json` flag. Tests assert on `Display` values first, and on rendered text only for the two renderers.

### D4. The Windows Terminal indicator is a function from the tracker to an escape sequence

`TerminalProgress.indicator : Tracker -> Indicator` (hidden, indeterminate, or a percentage from 0 to 100) and `TerminalProgress.sequence : Indicator -> string` are pure. The renderer writes the sequence only when `OutputMode` is interactive and the `WT_SESSION` environment variable is set, and writes the clearing sequence from the `finally` of `withProgress` so that completion, cancellation and failure all clear it.

### D5. Sleep prevention is a bracket in `Vara.Engine`

`PowerRequest.withSleepPrevention : (string -> unit) -> (unit -> Task<'a>) -> Task<'a>` creates a Win32 power request (`PowerCreateRequest`, `PowerSetRequest` for the system-required type only, so the display is not kept on), runs the body, and clears the request in a `finally`. Creating or setting the request returns a value, `Result<handle, win32 error>`, and a failure produces a warning on standard error and runs the body without the request. A failure to clear the request is also reported to standard error and never replaces the body's result or exception. The interop uses `DllImport` with a blittable `REASON_CONTEXT` struct and a `SafeHandle` subclass, the one class this module needs. A dry run does not call it.

### D6. JSON is built as a value and written once

`RunJson.ofRun` and `RunJson.ofPlan` build a small `JsonValue` union from `RunResult` and `PlanSummary`; one function writes it with `Utf8JsonWriter`, bypassing the console so that the object cannot be wrapped. Field names and order are those of the C# `BackupRunSummaryJson`, and a parity test compares the output of both builds for the same run, with the volatile fields (timestamps, durations) normalized. `--json` selects `OutputMode.Json`, which installs no observers at all.

### D7. The engine gains the two observers the display cannot get any other way

The display needs the plan's totals when the transfer phase starts, and a count of entries seen while scanning (both the real run's and the dry run's scan phase). `Observers.TransferPhaseStarting` takes the plan's totals as a struct, and a new `Observers.ScanProgress` takes a running entry count. Both are optional in the same sense as `BytesCopied`: with `Observers.none` they cost nothing. The scan count is taken by wrapping the scan sequence in the composition of `planFor`, not inside the scanner, so the scanner keeps no state.

### D8. Gate B measurement method

Gate B compares the Native AOT `vara.exe` of each build, published the same way, on a quiet machine. The method is fixed here so that the result does not depend on who runs it.

- **Script:** `benchmarks/Measure-GateB.ps1` takes the two executable paths, a scratch directory and a run count. It generates the fixtures, runs the scenarios, and prints a table. It is committed with the result, in a new baseline folder.
- **Scenarios:** (1) a first backup of a fixture of 2,000 files of 1 to 64 KB; (2) a second backup of the same source with nothing changed; (3) a first backup of a large tree of 100,000 or more files of 1 to 4 KB, in 1,000 directories. All use the real target filesystem (the system drive's temp directory), never an in-memory one.
- **Order and count:** 15 runs per scenario per build, alternating (C#, F#, F#, C#, and so on) so that drift hits both equally. Each run starts from a fresh target; the first run of each scenario is a warm-up and is discarded. The source fixture is generated once and read from the file cache after the warm-up for both builds.
- **Metrics:** wall-clock time measured around the process (median and interquartile range), peak working set from the process, and executable size on disk.
- **Pass rule for time:** the median of F# is at most 1.05 times the median of C# in each scenario. A scenario whose interquartile range is above 5% of its median is not conclusive: it is rerun once after the noise source is found (antimalware scanning of the fixture directory is the known one; an exclusion for the scratch directory is allowed when policy permits and is recorded). It is never passed on one lucky run.
- **Memory:** peak working set of the F# build at most 1.10 times that of C# in the large-tree scenario, and recorded for the others.
- **Executable size:** at most 1.25 times the C# executable, and in any case recorded. The F# executable with the engine is 6.5 MB (the skeleton alone is 3.1 MB); the C# size is measured in the same task.
- **AOT:** zero IL2xxx and IL3xxx warnings for the full publish, and the publish runs the smoke commands (`--help`, a real backup, a dry run, `--json`).
- **If the gate fails:** the series stops, as `fsharp-backup-engine`'s roadmap says. The report names the scenario and metric that failed, so that the cause can be profiled before the decision.

### D9. Presentation parity is checked at the edges that can be compared

The progress display is interactive and depends on width and timing, so it is checked through `Display` values and the renderer tests, not by comparing screens with C#. JSON output, exit codes and the plain-text fallback of the final summary are compared directly between the two builds in the parity suite.

## Risks / Trade-offs

- [Gate B fails on time because of the machine, not the code] → The method alternates builds, discards warm-ups and treats a noisy scenario as inconclusive, so that a noisy machine produces a rerun and not a verdict. Allocation parity is already held by the engine's budget, so a time failure is investigated with a profile before it is accepted.
- [The display re-renders more often than C# and costs time in a large run] → Redraws are rate-limited by `Throttle.admit` and happen on the recorder's schedule only when a display exists. `Observers.none` is used for JSON and for redirected output, which is also what the large-tree Gate B scenario measures first.
- [A Spectre.Console live display is not AOT-clean in some version] → The skeleton already publishes with Spectre.Console and zero IL warnings; the publish task verifies it again with the live renderer reachable.
- [The dry run's UNC behavior differs from C#] → Established by `fsharp-backup-engine`: a reachable UNC entry fails at scan time in F# and is reported as unscanned, while C# plans it as an add. The dry-run display here lists unscanned paths as C# does for other unreadable paths. The behavior is aligned in this change if a scenario or the parity check demands it; otherwise it is recorded in the commit message.
- [The tracker cell is mutable state] → It lives inside `withProgress`, is updated only from the observers, which the engine calls one at a time, and is read by the renderer's tick through the same function. No other function sees it.

## Migration Plan

Nothing ships. The C# `vara.exe` stays the distributed build; the F# one is published to `artifacts/` for the Gate B measurement. To roll back, revert this change's commits.

## Open Questions

- Whether the Gate B script is kept after the cutover as a release check. It is decided in `fsharp-cutover`.
