## Why

Backup progress currently only updates when a file-transfer chunk actually completes. If a transfer stalls for any reason (a slow or hung disk read, a network share hiccup, a lock contended by another process), no chunk event fires, so the displayed bytes-transferred, throughput, and ETA freeze at their last-computed values even though real time keeps passing - giving the same "is this hung or just slow?" uncertainty the stream-large-file-transfer-progress change set out to eliminate, just resurfacing whenever the underlying transfer activity itself pauses rather than merely runs slowly.

## What Changes

- Introduce a wall-clock heartbeat, independent of the transfer threads, that periodically re-presents the most recently observed progress value through the existing display pipeline while a run is in progress, so throughput and ETA keep recalculating against current elapsed time even when no new bytes have moved.
- No change to how byte-level progress is generated or rate-limited during genuine forward progress - the existing chunk-driven reporting and the 100ms redraw gate are unaffected; the heartbeat only fills the gap when chunk events stop arriving.

## Capabilities

### New Capabilities

(none)

### Modified Capabilities

- `progress-reporting`: the displayed throughput and ETA must keep updating against elapsed wall-clock time even when a transfer stalls and no new bytes have been reported for a period, not only when a new byte-progress event arrives.

## Impact

- `Vara.Cli.Commands.BackupCommand`: gains a periodic timer that re-reports the last known `BackupProgress` value through the existing `ProgressDisplayGate`/`BackupProgressCalculator` pipeline while the run is active, and stops once the run completes.
- No change to `Vara.Application.Reporting.ProgressDisplayGate` or `Vara.Application.Reporting.BackupProgressCalculator` - both already tolerate a repeated (non-decreasing) byte value and recompute throughput/ETA from fresh wall-clock time on every admitted report, so the heartbeat can reuse them as-is.
- No change to `Vara.Application.Backup.BackupExecutor`, `BackupPipeline`, or the content-store streaming path from the stream-large-file-transfer-progress change - this change only adds a display-side heartbeat, not a new source of byte-level progress.
