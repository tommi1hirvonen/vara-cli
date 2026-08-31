## Why

`BackupPlanner`'s move-detection pass hashes every size-matched candidate file on a single thread, fully up front, before the parallel transfer stage even starts. Files that turn out not to be moves get read in full for this test alone, on top of the read the transfer stage separately performs to actually copy them. At small scale this is invisible; at large scale - especially the large-scale reorganizations move-detection exists for (renamed folder trees, reshuffled media/log/disk-image collections) - it's a serial, full-file-read bottleneck that neither takes advantage of available parallelism nor avoids reading content it doesn't need to.

## What Changes

- Parallelize `BackupPlanner`'s move-detection hashing: compute hashes for all size-matched `Added` candidates concurrently (mirroring the `Parallel.ForEach` pattern already used in `BackupExecutor`), then run the existing match logic serially over the precomputed results.
- Record a bounded-prefix "quick hash" (in addition to the existing full content hash) for every file version going forward, computed on bytes already being read/streamed so it costs no extra I/O. Move-detection uses it as a single-pass, early-abort pre-filter: read a candidate's leading bytes, hash them, and compare against still-viable same-size deleted candidates' stored quick hashes; only continue reading the rest of the file (to compute and compare the real full hash) when a quick hash actually matches. A quick hash match is never itself sufficient to declare a move - the full content hash must still agree.
- Tag every stored quick hash with a scheme identifier so a future change to the prefix window/algorithm can't produce false confirmations: rows recorded under a different scheme are simply treated as if no quick hash were recorded (always falls back to a full read for that specific candidate), with no migration/backfill step ever required.
- No change to move-detection's accuracy or which files are ultimately classified as moves - only how much is read, and how much of that reading is parallelized, to reach the same decision.

## Capabilities

### New Capabilities
_None._

### Modified Capabilities
- `backup-execution`: the manifest gains a per-file-version quick hash used to make move-detection's pre-filter concrete and observable (a file's version history now carries this alongside its existing content hash). See `specs/backup-execution/spec.md` in this change for the delta.

## Impact

- **Code**:
  - `src\Vara.Application\Backup\BackupPlanner.cs` - hashing pass restructured to run concurrently, with a single-pass streaming quick-hash/full-hash helper (early-abort when the quick hash can't match).
  - `src\Vara.Core\Snapshots\FileVersionRecord.cs` - `FileVersionRecord` and `CurrentFileState` gain nullable `QuickHash`/`QuickHashScheme` fields.
  - `src\Vara.Core\Abstractions\ISnapshotRepository.cs` - `RecordFileVersion` gains the new values.
  - `src\Vara.Infrastructure\Snapshots\SqliteSnapshotRepository.cs` - manifest schema gains the new nullable column(s).
  - `src\Vara.Infrastructure\Storage\FileSystemContentStore.cs` / `src\Vara.Application\Backup\BackupExecutor.cs` - compute the quick hash on the same bytes already streamed when storing new/changed content, so newly-recorded rows carry it forward for future runs.
  - No change to `IHasher`'s existing single-call contract; the streaming quick-hash/full-hash computation is a new, separate helper.
- **Tests**: `tests\Vara.Application.Tests\Backup\BackupPlannerTests.cs`, `tests\Vara.Infrastructure.Tests\Snapshots\SqliteSnapshotRepositoryTests.cs`, `tests\Vara.Infrastructure.Tests\Storage\FileSystemContentStoreTests.cs` - existing move-detection and manifest correctness tests must keep passing unmodified (behavior-preserving); new tests added for the parallel path, the early-abort pre-filter, and scheme-mismatch fallback.
- **Migration**: none - this project is unreleased, so the new manifest column(s) can be added directly with no backfill for existing rows.
- **Dependencies**: none added.
- **Risk**: concurrent hashing must not introduce races around `consumedAsMoveSource`/candidate matching; addressed by keeping the matching/consumption logic serial and only parallelizing the read-and-hash step (see design.md).
