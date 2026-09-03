## Context

See `proposal.md` for motivation. `PruneService.Prune` currently runs, inside one `runLock`-held block, with no progress feedback at all: `ListSnapshots` (DB query) -> `DetermineEligibleForRemoval` (in-memory) -> `PruneSnapshots` (one bulk SQL `DELETE` transaction, not meaningfully iterable) -> a hash-diff (`ListAllStoredHashes().Except(referencedHashes)`, in-memory) -> a `foreach` loop calling `contentStore.DeleteContent(hash)` once per unreferenced blob. Everything before that final loop is normally fast (a handful of DB/in-memory operations); the loop is the only part whose duration scales with the size of the content store, and it is inherently a discrete-item operation (one file delete per iteration), not a byte transfer - there is no meaningful "total bytes" figure to sum for it. `PruneCommand` separately calls `CountEligibleForRemoval` before `Prune`, to decide whether to prompt for confirmation - that call is unrelated to this change (a single fast query, already complete before `Prune` itself runs).

## Goals / Non-Goals

**Goals:**
- An indeterminate progress indicator is shown for the DB/diff steps preceding blob deletion, whose duration isn't practically predictable upfront.
- Once the set of unreferenced blobs is known, a count-based indicator ("Deleting blobs x / y") replaces the spinner, updating as each blob is deleted.
- `PruneService.Prune` reports this progress via a top-level `IProgress<T>` parameter, mirroring `BackupPipeline.Run(profile, IProgress<BackupProgress>)`'s existing convention for a pipeline's outermost progress-reporting API.
- Non-interactive/redirected output falls back to plain output, reusing `OutputMode.IsLiveCapable`.

**Non-Goals:**
- No progress indication for the pre-confirmation `CountEligibleForRemoval` preview call in `PruneCommand` - it is a single fast query, separate from and prior to `Prune`'s own work.
- No attempt to make `PruneSnapshots`'s bulk SQL `DELETE` itself report incremental progress - it is one transaction, not iterable in any way that would produce meaningful intermediate progress.
- No byte-based sizing of the blob-deletion phase (e.g. summing deleted blobs' sizes) - deletions are reported as a count, per the `progress-reporting` capability's new count-based requirement, not as bytes.

## Decisions

### `PruneService.Prune` takes an `IProgress<PruneProgress>?` parameter; `PruneProgress` reports only the deletion phase
```
public sealed record PruneProgress(int BlobsDeleted, int TotalBlobs);
```
`Prune` reports once immediately before the deletion loop starts (`BlobsDeleted = 0`, `TotalBlobs = unreferencedHashes.Count`) and once after each blob is deleted. No report is sent for the earlier DB/diff steps - `PruneCommand` shows its own indeterminate spinner unconditionally from the moment it calls `Prune`, and simply swaps that spinner for the count-based indicator on the first received report, the same "replace the indeterminate indicator on the first admitted event" pattern `BackupCommand` already uses for its scan-to-transfer transition.

Alternatives considered: a phase enum (`Evaluating`/`DeletingBlobs`) reported throughout - rejected as unnecessary; the CLI already knows it is in the "evaluating" state simply by virtue of not yet having called `Prune`'s deletion phase, exactly like `BackupCommand` never needs `BackupPipeline` to explicitly announce "I am scanning now" before its first real progress report.

### No rate-limiting gate is needed for the deletion phase - rely on Spectre's own `AutoRefresh`
Unlike backup's byte-progress reporting (which calls `ctx.Refresh()` explicitly inside its render callback, needing `ProgressDisplayGate` to cap how often that expensive redraw happens), prune's `onBlobDeleted` callback only updates an in-memory `ProgressTask.Value` (a cheap integer increment) on every iteration - it never forces an explicit `ctx.Refresh()` itself. Spectre's `Progress()` region already redraws on its own periodic timer (`AutoRefresh`) independent of how often the underlying value changes, so the display's redraw rate is naturally bounded without introducing a prune-specific rate limiter. A single explicit `ctx.Refresh()` after the deletion loop completes ensures the final count is shown immediately rather than waiting for the next timer tick.

Alternatives considered: reusing `ProgressDisplayGate` (constructing synthetic `Backup.BackupProgress` values with `BlobsDeleted`/`TotalBlobs` in place of bytes) - rejected as needless: that gate exists specifically to cap the cost of *forced* redraws, which prune's callback never performs.

### `PruneCommand`'s live display uses Spectre's stock columns, driving the task's `Description` for the "x / y" text
`console.Progress().Columns(new TaskDescriptionColumn(), new ProgressBarColumn())` - no `PercentageColumn`. The task's `Description` is set to `"Evaluating..."` when the run starts and IsIndeterminate is true, then updated to `"Deleting blobs {BlobsDeleted} / {TotalBlobs}"` on each report (with `IsIndeterminate` cleared and `MaxValue`/`Value` set from the same report), so the bar visually fills in proportion to blobs deleted while the description shows the literal counts the user asked for, rather than a percentage. `TaskDescriptionColumn` already renders to the left of `ProgressBarColumn` by Spectre's default column ordering, so no custom `ProgressColumn` is needed to achieve a leading label - the same reasoning `add-restore-progress-reporting`'s design.md uses for restore's single-row display applies here: prune's display, across its lifetime, is one task reused sequentially (spinner, then count-based bar), not several differently-shaped rows sharing one row-width budget, so `BackupProgressColumn`'s bespoke multi-role rendering is not needed.

### Zero unreferenced blobs: no deletion-phase report is sent at all
WHEN `unreferencedHashes.Count == 0`, `Prune` skips the pre-loop report entirely (there is nothing to show a count-based indicator for) - the spinner simply remains on screen until the method returns and the live region closes, consistent with the `progress-reporting` capability's "No unreferenced content to remove" scenario.

## Risks / Trade-offs

- [Risk] `IProgress<T>.Report` marshals its callback via the captured `SynchronizationContext` (as `BackupPipeline.Run` already relies on for `IProgress<BackupProgress>`), which could reorder or delay reports under unusual hosting scenarios → Mitigation: `Prune`'s deletion loop is single-threaded and strictly sequential (unlike backup's concurrent workers), so reports are already produced in order; this is a pre-existing, accepted characteristic of `IProgress<T>` the codebase already uses elsewhere.
- [Trade-off] Relying on `AutoRefresh` instead of an explicit rate limiter means the exact redraw cadence during the deletion phase is Spectre's default, not a value this change tunes directly → acceptable; `BackupCommand`'s existing heartbeat/rate-limit tuning was needed because it *forces* a refresh per admitted event, which prune's design deliberately avoids doing.
