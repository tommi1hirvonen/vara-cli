## Context

See `proposal.md` for motivation. Today, `IContentStore.ExtractTo(hash, destinationAbsolutePath)` performs a single `File.Copy(blobPath, destinationAbsolutePath, overwrite: true)` call with no progress callback at all - unlike `StoreFromStream` and `PlaceAtMirrorPath`, which already report incremental progress via an `Action<long>? onBytesCopied` parameter, using a private `CopyWithProgress` helper (`BufferedStream` + `TeeStream` + `CopyTo(Stream.Null)`) introduced by the earlier `stream-large-file-transfer-progress` change. `SnapshotHistoryService.RestoreAsOf`/`RestoreVersion` already have the destination version's size available via `FileVersionRecord.Size` before calling `ExtractTo`, so - unlike backup - the total byte count for a restore is known immediately; no scan/plan phase is needed.

`BackupCommand`'s live display uses a custom `BackupProgressColumn` (one `ProgressColumn` shared across three synthetic per-role tasks: scan/bar/stats) specifically because those three tasks need differently-shaped rows sharing one row-width budget - using Spectre's normal multi-column `Progress()` layout would reserve every configured column's width on every task's row, including blank space on rows that don't need that column. Restore has no such requirement: it is exactly one file, one row, for the run's duration.

## Goals / Non-Goals

**Goals:**
- `ExtractTo` reports incremental progress the same way `StoreFromStream`/`PlaceAtMirrorPath` already do, reusing the existing `CopyWithProgress` mechanism rather than introducing a second copy-with-progress implementation.
- Restore's live progress bar starts immediately at a known total (no indeterminate phase), since the version's size is already known before extraction begins.
- Restore's live display is implemented with Spectre's own stock progress columns, not a bespoke `ProgressColumn` subclass - there is only one row's worth of content, so the complexity `BackupProgressColumn` exists to solve does not apply here.
- Non-interactive/redirected output falls back to plain output, reusing the existing `OutputMode.IsLiveCapable` check `BackupCommand` already uses for the same decision.

**Non-Goals:**
- No outcome-severity coloring (success/partial-failure/error) the way backup's bar has - a restore either succeeds or throws a hard error before completing (there is no partial-success concept for a single file), so the bar uses one consistent color throughout and errors continue to propagate to the existing `ErrorReporting.Run` catch-all unchanged.
- No change to how directory restores would work - out of scope until directory restore itself is proposed; this change only instruments the existing single-file `ExtractTo` path.
- No change to `BackupProgressColumn`, `BackupCommand`, or the `backup-progress-label-placement` change's layout decisions - the two changes are independent; restore does not reuse backup's rendering column.

## Decisions

### `ExtractTo` gains an optional `onBytesCopied` callback, reusing the existing `CopyWithProgress` helper
`IContentStore.ExtractTo(string hash, string destinationAbsolutePath, Action<long>? onBytesCopied = null)` - the new parameter defaults to `null`, so no existing caller breaks. The implementation switches from `File.Copy` to the same private `CopyWithProgress(sourcePath, destinationPath, onBytesCopied)` helper `PlaceAtMirrorPath`'s real-copy fallback already uses, keeping exactly one chunked-copy-with-progress implementation in `FileSystemContentStore` rather than two.

`CopyWithProgress` today opens its destination with `FileMode.CreateNew` (safe for `PlaceAtMirrorPath`, which always writes to a fresh staging path before an atomic rename), which would throw if the destination already exists - unlike `ExtractTo`'s current `overwrite: true` behavior. `ExtractTo` deletes an existing destination file, if present, immediately before calling `CopyWithProgress`, preserving today's overwrite semantics exactly (already gated by `SnapshotHistoryService.GuardDestination`, which only lets execution reach `ExtractTo` for an existing destination when the caller has explicitly authorized overwriting it).

Alternatives considered: adding a `FileMode` parameter to `CopyWithProgress` to let it overwrite in place - rejected as unnecessary indirection; a plain delete-then-create is simpler and matches what `File.Copy(overwrite: true)` effectively does today (replace the destination's content).

### `SnapshotHistoryService.RestoreAsOf`/`RestoreVersion` forward an optional progress callback straight through
Both methods gain an `Action<long>? onBytesCopied = null` parameter (or a single shared options-style parameter, decided at implementation time if more than one callback-shaped parameter is ever needed) passed straight to `contentStore.ExtractTo`. No new abstraction is introduced here - this mirrors how `BackupPipeline` already threads a progress delegate down to `IContentStore` calls.

### `RestoreCommand`'s live display uses Spectre's stock columns directly, not a custom `ProgressColumn`
`console.Progress().Columns(new TaskDescriptionColumn(), new ProgressBarColumn(), new PercentageColumn())` (column set finalized at implementation time - `RemainingTimeColumn`/`TransferSpeedColumn` may also be included, since ETA/throughput are valuable for a large single-file restore and Spectre provides both natively without needing `BackupProgressCalculator`'s hand-rolled math). One task is created with `maxValue` set to the version's known `Size` up front; `onBytesCopied` increments that task's `Value`. This is a materially simpler design than `BackupProgressColumn` because there is only one task/row for the entire run - none of the role-switching, outcome-recoloring, or shared-row-budget concerns that motivated `BackupProgressColumn`'s custom rendering apply here.

Alternatives considered: reusing `BackupProgressColumn`/`BackupProgressRole` directly - rejected, that column's `Render` branches on roles (`Scan`/`Bar`/`Stats`) restore doesn't have, and its width-budget-sharing design solves a problem (three differently-shaped rows) that doesn't exist for a single-row restore display.

### Rate-limiting reuses `ProgressDisplayGate`, constructing `Backup.BackupProgress` instances
`ProgressDisplayGate.Report` and `Backup.BackupProgress` are reused as-is - a restore's `onBytesCopied` callback constructs a `Backup.BackupProgress(bytesCopied, totalBytes)` and passes it through the same gate `BackupCommand` uses, gaining redraw rate-limiting for free. The gate's monotonic-value guard is unnecessary here (a single sequential stream copy, unlike backup's concurrent worker threads, can never deliver progress out of order), but is harmless to keep - it never rejects an in-order, always-increasing sequence of updates.

Alternatives considered: writing a restore-specific progress-rate-limiting type - rejected as needless duplication of already-tested logic for a difference (concurrency) that doesn't change the gate's externally observable behavior for restore's use case.

## Risks / Trade-offs

- [Risk] Switching `ExtractTo`'s overwrite path from a single `File.Copy(overwrite: true)` call to delete-then-`CopyWithProgress` means a crash mid-copy could leave a partially-written destination file where a fully-succeeded or fully-failed `File.Copy` would not have → Mitigation: `File.Copy` itself offers no atomicity guarantee against a mid-copy crash either (it's a buffered copy internally); this is not a regression this change introduces, and no existing requirement mandates atomic restore writes.
- [Trade-off] Reusing `Backup.BackupProgress`/`ProgressDisplayGate` for a non-backup (restore) code path is a naming mismatch (a "backup" type used outside the backup pipeline) → accepted to avoid duplicating tested math/rate-limiting logic; a follow-up rename to a capability-neutral name (e.g. `ByteProgress`) is possible later but out of scope here, since it would ripple through the entire backup pipeline for a cosmetic-only benefit.
