## Why

`vara backup --dry-run` can spend significant time scanning and hashing files but currently gives no feedback until planning finishes. An indeterminate indicator would make it clear that the dry run is active without suggesting a known completion percentage.

## What Changes

- Show an indeterminate scan indicator during a human-readable dry run when the console supports live progress.
- Keep JSON output free of progress rendering, and preserve the existing behavior when live progress is unavailable.

## Capabilities

### New Capabilities

None.

### Modified Capabilities

- `progress-reporting`: Specify live indeterminate scan feedback for dry-run planning.

## Impact

- Affects the backup CLI's dry-run output and its terminal progress presentation.
- Reuses the existing progress display and does not require new dependencies or public APIs.
