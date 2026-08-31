## Why

An AI review of the solution flagged that `BackupCommand` wires backup progress through
a plain `Progress<BackupProgress>` whose handler calls `Console.Write` directly. A
targeted repro (2000 concurrent `Report()` calls mimicking `BackupExecutor`'s parallel
transfer loop) confirmed the underlying mechanism: `Progress<T>` posts each report to
the thread pool with no ordering guarantee, and up to 24 handler invocations ran
concurrently with ~47% of deliveries arriving out of order. `Console.Write` itself is
internally synchronized, so lines are not character-garbled, but a stale (older) byte
count can be displayed after a newer one, making the progress line flicker backward.
Separately, progress is reported once per file with no throttling, so a run with many
small files can refresh the line far faster than a human can read it. Both issues
degrade the trustworthiness of the progress display the `progress-reporting` capability
promises.

## What Changes

- Sequence backup progress reports so a late-arriving, stale report can never overwrite
  a more recent one on screen (e.g. attach a monotonically increasing sequence number or
  cumulative byte count and drop out-of-order deliveries at the display layer).
- Rate-limit how often the progress line is actually redrawn (time-based throttle),
  independent of how many individual file-transfer callbacks fire, while still updating
  immediately at the start and end of a run.
- Preserve existing behavior for run summary statistics, which are computed from final
  counters rather than the progress stream and are unaffected by this change.

## Capabilities

### New Capabilities

(none)

### Modified Capabilities

- `progress-reporting`: adds requirements that displayed progress must never regress to
  a stale value once a more recent value has been shown, and that progress display
  updates are rate-limited rather than redrawn on every individual file transfer.

## Impact

- `src/Vara.Cli/Commands/BackupCommand.cs`: how progress is consumed and rendered.
- `src/Vara.Application/Reporting/BackupProgressCalculator.cs` (and/or a new
  presentation-layer helper): where sequencing/throttling logic is added.
- `src/Vara.Application/Backup/BackupExecutor.cs`: unaffected in behavior, but is the
  source of the high-frequency, multi-threaded `Report()` calls this change addresses.
- No changes to on-disk manifest format, CLI arguments, or run summary output.
