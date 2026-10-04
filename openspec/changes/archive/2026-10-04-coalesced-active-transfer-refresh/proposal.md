## Why

The backup display can miss short transfers because active paths are currently rendered only as part of throttled numeric-progress updates. Refreshing on every path change would make the numeric statistics harder to read and could create unnecessary rendering work, so active-path refreshes need an independent, coalesced path.

## What Changes

- Refresh the live active-transfer list in response to file start and finish events, independently of the numeric-statistics update cadence.
- Coalesce pending path-refresh requests and use the latest active-path state when rendering, so stale events do not display completed files and event bursts do not create an unbounded rendering queue.
- Keep numeric statistics, including the completed-file count, on the existing readable update cadence rather than refreshing them for each file lifecycle event.
- Treat active-path visibility as best effort: if a transfer finishes before a truthful refresh can be rendered, it may not appear.

## Capabilities

### New Capabilities

*(none)*

### Modified Capabilities

- `progress-reporting`: Separate active-path refreshes from rate-limited numeric-stat updates while ensuring displayed active paths reflect the latest in-flight state.

## Impact

- `Vara.Application` — file lifecycle progress events and active-path state propagation in `BackupExecutor` and `BackupPipeline`.
- `Vara.Cli` — coalesced refresh coordination and merging the latest active paths with the latest rate-admitted numeric statistics in the live display.
- `openspec/specs/progress-reporting/spec.md` — clarify rate limits for numeric statistics versus active-path redraws and specify the truthful, best-effort behavior for very short transfers.
- No new dependencies or public API break is intended.
