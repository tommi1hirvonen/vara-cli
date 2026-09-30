## Context

See proposal.md for motivation and problem background.

In the `stream-large-file-transfer-progress` change, chunk-level progress callbacks were wired into both `IContentStore.StoreFromStream` and `IContentStore.PlaceAtMirrorPath`. On filesystems without hardlink support (`SupportsHardlinks == false`), both calls stream the entire file's bytes. To prevent progress percentage from exceeding 100%, `BackupPipeline.Run` doubled `plan.TotalBytesToTransfer` into `progressTotalBytes`.

While this prevented the percentage from exceeding 100%, it created a glaring user-visible inconsistency:
- Dry run (`BackupPipeline.PlanOnly`) reports `plan.TotalBytesToTransfer` (e.g., 15.00 GB).
- Live progress displays `x GB / 30.00 GB`.
- Completion summary reports `counts.BytesTransferred` (15.00 GB).
- Calculated throughput was doubled because twice as many bytes were reported over elapsed wall-clock time.

## Goals / Non-Goals

**Goals:**
- Unify the total bytes figure across dry-run, live backup progress, and run completion summary so all three report `plan.TotalBytesToTransfer`.
- Ensure live progress advances smoothly during both the source-read phase (`StoreFromStream`) and the mirror-copy phase (`PlaceAtMirrorPath`) when hardlinks are unsupported, without stalling or freezing.
- Guarantee that reported cumulative bytes for any file never exceeds that file's actual size, and cumulative bytes for the run equals `plan.TotalBytesToTransfer`.
- Ensure reported progress percentage never exceeds 100%.

**Non-Goals:**
- Changing `IContentStore` or `FileSystemContentStore` interface signatures.
- Adding asynchronous I/O or multi-stage UI presentation changes.
- Altering the scan phase or non-byte operations (moves/deletions/pruning).

## Decisions

### 1. Progress denominator is always `plan.TotalBytesToTransfer`
`BackupPipeline.Run` will no longer branch on `contentStore.SupportsHardlinks` when computing `progressTotalBytes`:
```csharp
var progressTotalBytes = plan.TotalBytesToTransfer;
```
This guarantees alignment across dry-run (`BackupPlanSummary.TotalBytesToTransfer`), live progress (`BackupProgress.TotalBytes`), and completion summary (`SnapshotStats.BytesTransferred`).

### 2. Proportional progress scaling in `BackupExecutor` when `SupportsHardlinks == false`
When `contentStore.SupportsHardlinks` is false, each file transfer consists of two streamed passes:
1. `StoreFromStream`: Reading source content and writing content blob (50% of the file's progress).
2. `PlaceAtMirrorPath`: Copying content blob to mirror path (remaining 50% of the file's progress).

To implement this cleanly without changing `IContentStore`:
`BackupExecutor.ExecuteTransfer` adapts `onBytesTransferred` using a local per-file progress scaler:
- As chunks arrive from `StoreFromStream`, chunk increments are scaled so cumulative progress from this pass reaches `fileSize / 2`.
- As chunks arrive from `PlaceAtMirrorPath`, chunk increments are scaled so cumulative progress from this pass reaches `fileSize / 2`.
- Upon completion of `PlaceAtMirrorPath`, any integer remainder (`fileSize - reportedSoFar`, such as 1 byte for an odd file size) is emitted so the cumulative progress reported for the file equals `fileSize` exactly.

*Alternative considered:* Only pass progress to `StoreFromStream` and pass `null` to `PlaceAtMirrorPath`.
*Rejected:* On USB/exFAT targets, copying a 20 GB file to the mirror path takes considerable time. Passing `null` would cause the progress bar to reach 100% and then freeze for minutes during mirror placement, reintroducing the freeze bug that `stream-large-file-transfer-progress` was created to fix.

### 3. Hardlink fallback copy on hardlink-capable volumes
When `contentStore.SupportsHardlinks` is true, `StoreFromStream` reports 100% of the file's bytes. In the vast majority of cases, `PlaceAtMirrorPath` creates an instant hardlink without streaming bytes.
If a specific blob hits NTFS's 1024-hardlink cap and `PlaceAtMirrorPath` falls back to `CopyWithProgress`:
- `PlaceAtMirrorPath` is called with `onBytesCopied = null` (or ignored) when `SupportsHardlinks` is true.
- Because the file's full size was already reported during `StoreFromStream`, suppressing the fallback copy's progress callback ensures the file's bytes are never double-counted, preventing the progress percentage from overshooting 100%.

### 4. Concurrency and thread-safety
`BackupPipeline.Run` accumulates bytes across threads using `Interlocked.Add(ref bytesSoFar, transferred)`.
Because each file's progress scaler produces non-negative deltas whose sum per file equals `fileSize`, concurrent file transfers safely advance `bytesSoFar` monotonically up to `plan.TotalBytesToTransfer`.

## Risks / Trade-offs

- [Integer rounding on small or odd-sized files during 50/50 split] → Mitigated by remainder reconciliation at the end of `PlaceAtMirrorPath`, ensuring total reported per file is exact.
- [Slightly more frequent, smaller progress callbacks when scaled by 2] → Progress callbacks are lightweight in-memory additions and rate-limited at the display layer by `ProgressDisplayGate` (100ms throttle).
