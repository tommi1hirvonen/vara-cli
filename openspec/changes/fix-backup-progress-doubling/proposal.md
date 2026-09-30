## Why

When backing up to a filesystem or target that does not support hardlinks (such as exFAT, FAT32, or across filesystem boundaries), the live backup status displays twice the actual transferred bytes (for example, `x GB / 30.00 GB` instead of `x GB / 15.00 GB`). This directly contradicts the dry run report (which reports `15.00 GB to transfer`) and the final summary upon completion (which correctly reports `15.00 GB transferred`).

This discrepancy was introduced as a workaround in `stream-large-file-transfer-progress`: to prevent the progress percentage from exceeding 100% when progress callbacks fired for both the store-read pass and the mirror-copy pass, the total denominator in `BackupPipeline` was simply doubled (`plan.TotalBytesToTransfer * 2`). This confuses users by making it appear as though the backup tool is transferring or duplicating twice as much data, and it skews the live throughput and ETA figures.

## What Changes

- **Consistent Total Bytes Denominator**: The live progress total denominator (`BackupProgress.TotalBytes`) will always equal the planned source bytes to transfer (`plan.TotalBytesToTransfer`), matching the dry-run output and the post-backup completion summary across all filesystems.
- **Proportional Progress for Fallback Copies**: When a target filesystem does not support hardlinks (or when placing a mirror file requires a full fallback copy), each copy pass (source read into content store, and content store copy into mirror) contributes proportionally (50% each) toward that file's transferred byte progress.
- **Eliminate Progress Overshoot**: On hardlink-capable systems where a specific blob reaches the hardlink limit and falls back to a streamed copy, progress is appropriately scaled or bounded so the reported percentage never exceeds 100%.
- **Accurate Throughput Calculation**: Live throughput calculations reflect the logical data transfer rate (source data processed over elapsed time) rather than inflated raw physical disk I/O.

## Capabilities

### New Capabilities

*(none)*

### Modified Capabilities

- `progress-reporting`: Modifies the requirement for incremental progress during fallback copies so that the total-bytes-to-transfer figure remains equal to the logical source bytes to transfer, and fallback copy passes contribute proportionally rather than doubling the denominator.

## Impact

- `Vara.Application.Backup.BackupPipeline`: Stop doubling `plan.TotalBytesToTransfer` when `contentStore.SupportsHardlinks` is false.
- `Vara.Application.Backup.BackupExecutor`: When hardlinks are not supported (or during fallback copies), scale chunk progress callbacks so that each phase contributes half of the file's bytes, with remainder reconciliation on completion to ensure the exact byte total is reached without rounding error.
- Tests in `Vara.Application.Tests` and `Vara.IntegrationTests`: Update tests asserting progress reporting under non-hardlink conditions to verify that total bytes equals `plan.TotalBytesToTransfer` and max reported bytes equals `plan.TotalBytesToTransfer`.
