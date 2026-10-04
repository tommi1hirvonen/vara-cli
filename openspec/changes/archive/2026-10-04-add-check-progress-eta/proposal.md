## Why

Large integrity checks can take a long time, and the current checked-blob count does not indicate the proportion of work completed or how long remains. A progress bar with a workload-aware estimate will make long checks easier to monitor, especially when blob sizes vary substantially.

## What Changes

- Add percentage and estimated-time-remaining reporting to the `check` command's live progress display.
- Use byte-weighted progress and observed read throughput for full verification, and observed per-blob timing for `--quick`, whose work is presence checks rather than content reads.
- Preserve the existing enumeration phase and checked/total blob count, and keep redirected output useful with plain-text progress updates.

## Capabilities

### New Capabilities

None.

### Modified Capabilities

- `backup-integrity`: specify progress percentage and ETA reporting for full and quick checks.

## Impact

- Affects the integrity check service and its progress data, the check command's live and plain-text presentation, and related unit/CLI tests.
- Uses the existing SQLite manifest's per-file-version sizes to determine the byte weight of each distinct referenced blob; no schema migration or external dependency is expected.
