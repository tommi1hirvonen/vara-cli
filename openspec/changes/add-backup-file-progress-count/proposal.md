## Why

The live backup display reports transferred bytes against planned bytes, but gives no direct indication of how many files remain. A file-count metric alongside byte progress helps users understand transfer progress when file sizes vary widely.

## What Changes

- Report successfully transferred files against the planned files whose contents will be transferred.
- Present the live statistics in two aligned rows so the additional metric does not overcrowd the existing bytes, throughput, and ETA fields.
- Include the file-count metric in non-interactive progress output as well.

## Capabilities

### New Capabilities

None.

### Modified Capabilities

- `progress-reporting`: Define file-transfer count progress and the expanded live and plain-text statistics.

## Impact

The backup execution progress data and CLI progress presenters are affected. No public command options, persisted data, or external dependencies are expected to change.
