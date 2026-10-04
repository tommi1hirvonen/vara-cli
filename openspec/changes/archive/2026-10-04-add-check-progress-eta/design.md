## Context

See proposal.md for motivation and specs/backup-integrity/spec.md for the observable contract. The check service currently enumerates distinct referenced hashes, enumerates stored hashes, and then verifies blobs sequentially. Progress is reported only after each blob. The manifest's `file_versions` table stores a size alongside each hash, while the content store exposes readable streams. Full verification therefore has both a useful upfront workload estimate and a way to measure actual read progress. Quick verification does not read content.

## Goals / Non-Goals

**Goals:**
- Make the live and redirected check displays expose a progress bar or textual percentage, blob counts, and an ETA.
- Make full-check progress and ETA account for varying blob sizes and the per-blob overhead that remains even for small or missing blobs.
- Reuse manifest metadata and actual stream reads without changing manifest contents or check outcomes.

**Non-Goals:**
- Persist check timings or introduce historical ETA calibration.
- Change integrity semantics, cancellation behavior, or the backup progress display.
- Add a schema migration or a new external dependency.

## Decisions

### Derive distinct blob weights from manifest sizes

Expose the size associated with each distinct referenced hash through the repository abstraction, using the existing `file_versions` data. Historical references to the same hash represent one verification item, not multiple byte weights; use one deterministic size per hash (the maximum recorded size, so inconsistent metadata cannot understate the work). In normal manifest data, repeated references to a hash have the same size. Do not sum every historical row, which would overstate progress for deduplicated content.

Alternatives considered:
- Counting blobs equally in full mode is inaccurate when sizes vary significantly.
- Reading each blob's current filesystem length for initial weights adds I/O and ignores the manifest's expected size.
- Adding a new persisted size index or check-history table is unnecessary for this estimate.

### Measure full-check byte progress while hashing

Track bytes consumed from each present blob's read stream during hashing and publish progress from the read path, throttled to avoid a callback for every small read. Keep the existing completed-blob count and update it only after each blob's check finishes. Progress percentage uses cumulative manifest-size weights: bytes read advance the current blob's weight, and a missing blob receives its full weight once its absence is established. Cap a blob's credited read bytes at its manifest weight so a size mismatch cannot push the percentage above 100. If the total manifest size is zero, fall back to completed-blob count; an empty check completes at 100 percent.

For the ETA, measure content-hash/read time separately from per-blob work outside that read (such as presence checks and opening the stream). Estimate remaining time as remaining present-blob bytes divided by observed hash/read throughput, plus remaining blob count multiplied by observed average per-blob overhead. Missing blobs already determined are removed from both remaining terms. Report the estimate as calculating until both timing components have usable observations; do not derive it from backup-run throughput or total blob count alone.

Alternatives considered:
- A single bytes-per-second rate folds item overhead into throughput and can badly misestimate checks with many small blobs.
- Total elapsed time divided by completed blob count does not account for the amount of data still to hash.
- An EWMA or sampled window could react more quickly to a changing workload, but adds tuning and can make the ETA less stable; begin with run-local observed rates and avoid exposing false precision.

### Use count-based timing for quick checks

Quick mode performs no content reads, so estimate remaining time from elapsed verification time per completed blob multiplied by the remaining blob count. Keep byte weights out of quick-mode percentage and ETA. Share the common display fields and formatting between modes while keeping their estimators mode-specific.

### Preserve the two output paths

Keep enumeration indeterminate until both referenced hashes and stored hashes are available. Once verification begins, render a live bar with percentage, blob count, and ETA on live-capable consoles. Keep plain-text appended updates for redirected output, adding percentage and ETA state there as well. Reuse the existing check command's display split and backup/restore formatting conventions rather than changing global output policy.

## Risks / Trade-offs

- [Manifest sizes may be inconsistent for the same hash] -> Use a deterministic maximum size per distinct hash and cap per-blob progress at that weight; normal records should agree.
- [Early estimates can be noisy, or workload characteristics can change mid-run] -> Show calculating until usable measurements exist, use run-local observations, and label the value as an estimate.
- [Progress callbacks from stream reads can be too frequent] -> Throttle updates by byte/time thresholds while always publishing blob completion and the final state.
- [Long individual blobs can otherwise appear stalled] -> Publish byte progress from stream reads, not only after a hash finishes.
- [Missing blobs have no read throughput] -> Count their manifest-weighted work as complete only after their absence has been established, and retain the blob count as an independent indicator.

## Migration Plan

No data migration is required. The implementation reads existing manifest sizes and remains compatible with existing profile databases.
