## Why

`BackupProgressCalculator` starts its elapsed-time clock (`_startedAt`) the moment the
transfer UI is created, which happens before `BackupExecutor.Execute` runs its
sequential Move/Delete pass. Move/Delete operations report zero bytes but still
consume real wall-clock time (mirror renames/removals, manifest writes), so any time
spent on them is folded into the throughput denominator as if it were part of the
byte-transfer phase. This permanently and irrecoverably depresses the run's
displayed throughput and inflates its ETA for the rest of the run - worse the larger
the Move/Delete batch and the shorter the run - contradicting the progress-reporting
spec's existing "Lightweight operations reported separately from data transfer"
requirement, which exists precisely so near-instant operations don't distort
throughput/ETA.

## What Changes

- Start the transfer throughput/ETA clock only when the first byte-transferring
  operation begins, not when the transfer UI/progress tasks are created - so time
  spent on the preceding Move/Delete pass is excluded from the throughput
  calculation entirely, matching how Move/Delete bytes are already excluded.
- No change to what is displayed during the Move/Delete pass itself (it already
  shows 0 bytes / no ETA); this only changes what elapsed time is charged against
  once byte transfer begins.

## Capabilities

### New Capabilities
(none)

### Modified Capabilities
- `progress-reporting`: sharpen the "Lightweight operations reported separately from
  data transfer" requirement so it also covers elapsed time, not just bytes - a
  Move/Delete pass preceding transfer must not count toward the throughput/ETA
  clock.

## Impact

- `Vara.Application/Reporting/BackupProgressCalculator.cs`: clock-start trigger.
- `Vara.Application/Backup/BackupPipeline.cs` and/or `BackupExecutor.cs`: need a
  signal for "byte transfer is about to begin" distinct from "Move/Delete is about
  to begin", since both currently precede the same first `progress.Report` call.
- `Vara.Cli/Commands/BackupCommand.cs`: none expected - it already just forwards
  whatever `BackupProgress` reports arrive to the calculator.
- Tests covering `BackupProgressCalculator` and the backup pipeline's progress
  reporting.
