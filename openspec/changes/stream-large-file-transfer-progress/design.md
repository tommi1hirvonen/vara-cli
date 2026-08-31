## Context

Today, `BackupExecutor.ExecuteTransfer` fires its `onBytesTransferred` callback exactly once per file, after `IContentStore.StoreFromStream` has already finished. `StoreFromStream` itself does two full sequential passes over a transferred file's bytes: `Stream.CopyTo` from the source into a temp file, then a second, separate read of that temp file to compute its content hash via `IHasher.ComputeHash(Stream)`. `PlaceAtMirrorPath`'s hardlink-fallback path (`File.Copy`) is a third, independent whole-file copy with no progress hook at all. `BackupPipeline.Run` accumulates a local `bytesSoFar` via a plain `+=` inside the lambda passed to `BackupExecutor.Execute`; this is only safe today because `BackupExecutor` invokes the callback from inside a lock that also serializes the per-file manifest write.

The existing `ProgressDisplayGate` (rate-limits redraws, ~100ms) and `BackupProgressCalculator` (derives percentage/throughput/ETA) already assume a stream of reports that may exceed a display-friendly rate and may arrive out of order across threads - the "Progress display never regresses to a stale value" and "Progress display updates are rate-limited" requirements were already built for exactly this. Nothing about those two components needs to change; the gap is entirely upstream, in how infrequently and coarsely progress *events* are generated in the first place.

See proposal.md - Why for the user-facing motivation.

## Goals / Non-Goals

**Goals:**
- Emit progress at sub-file granularity for every real, potentially slow, whole-file copy in the backup path: source-read-into-store, and mirror-placement fallback copy.
- Eliminate the redundant temp-file re-read that exists purely to compute the content hash, by hashing and writing in the same pass.
- Keep the change additive at the `IContentStore` interface boundary - no breaking signature changes for existing callers/tests that don't care about progress.
- Make the run's cumulative byte counter correct under concurrent chunk-level updates from multiple in-flight file transfers (relevant once `transfer_concurrency` is configured above its default of 1).

**Non-Goals:**
- Changing the redraw rate-limiting, monotonic-value guarding, or percentage/throughput/ETA formulas - `ProgressDisplayGate` and `BackupProgressCalculator` are unaffected and out of scope.
- Choosing a specific buffer/chunk size for the streamed copy - left as an implementation detail, tuned during implementation/testing rather than fixed here.
- Changing the quick-hash (`StreamingContentSignature`) used for move-detection pre-filtering - it already deliberately reads only a small bounded prefix and is unrelated to full-file transfer progress.
- Async I/O or cancellation support - out of scope; the pipeline is synchronous today and stays that way.

## Decisions

### Single streamed copy+hash pass via a tee stream
Replace `StoreFromStream`'s "`CopyTo` temp file, then reopen temp file to hash it" with a stream that, as it is read, simultaneously writes each chunk to the temp file and reports its size via a callback - then drive the whole thing with `_hasher.ComputeHash(teeStream)` instead of a separate `CopyTo` call. `XxHash128Hasher.ComputeHash` (via `NonCryptographicHashAlgorithm.Append(Stream)`) already just reads its input stream forward to EOF with no seeking, so it can safely sit on top of a tee stream. This removes the second full pass entirely, rather than merely instrumenting it - a strict improvement over "add progress to both passes."

Alternative considered: keep the two passes, and add a progress callback to each independently (report 0-50% during copy, 50-100% during hash readback). Rejected: still pays the redundant read's I/O cost, and produces a less accurate progress signal (bytes "transferred" would double-count against a file's true size unless carefully rescaled).

### `IContentStore.StoreFromStream` gains an optional progress callback
`(string Hash, long Size) StoreFromStream(Stream content, Action<long>? onBytesWritten = null)`. Optional parameter keeps this additive/non-breaking for any other caller or test. `FileSystemContentStore` is the only implementation and is updated to build the tee stream and invoke the callback per chunk.

### `PlaceAtMirrorPath`'s fallback copy uses the same tee mechanism
Both `File.Copy` call sites in `PlaceAtMirrorPath` (no-hardlink-support, and hardlink-limit-reached) are replaced with a manual streamed copy (open both files, tee reads from source into destination, same per-chunk callback) instead of `File.Copy`. This is a plain copy (no hash involved here, since the content's hash is already known), so no `IHasher` involvement - just the tee's write side and progress callback.

### Chunk granularity follows the reader's natural read-loop size
No new explicit chunking/throttling is introduced at the source. Progress fires once per underlying `Read()` call serviced by the tee (i.e. whatever chunk size `NonCryptographicHashAlgorithm.Append(Stream)`, or the manual copy loop for `PlaceAtMirrorPath`, requests per call). If that granularity risks shrinking the effective disk read size below what `Stream.CopyTo`'s default 80KB buffer achieves today, wrap the source in a `BufferedStream` with an explicit large buffer ahead of the tee, so small downstream read requests are still serviced from large upstream disk reads. The exact buffer size is an implementation detail (see Non-Goals); the design constraint is only that per-file disk throughput must not regress relative to today's `CopyTo`-based path. The existing 100ms `ProgressDisplayGate` redraw throttle already bounds how often any of this reaches the terminal, so firing progress on every internal chunk read is safe and requires no additional throttling at the source.

### Concurrency-safe cumulative counter
`BackupPipeline.Run`'s `bytesSoFar` becomes an `Interlocked`-managed counter (e.g. a boxed `long` field updated via `Interlocked.Add`, read via `Interlocked.Read` when constructing each `BackupProgress` report) instead of a captured-local `+=`. This is updated directly from chunk-level callbacks arriving from potentially concurrent file transfers, without taking `BackupExecutor`'s existing per-file `reportLock` (which continues to serialize only the once-per-file manifest write and summary `counts.BytesTransferred` accumulation, unchanged). Keeping the two counters/locks independent avoids the (much more frequent) progress pings from ever contending with SQLite manifest writes, and vice versa.

### Avoiding double-counting when a mirror placement also streams
`PlaceAtMirrorPath` runs for every Add/Change operation, immediately after `StoreFromStream` for the same file. Wiring chunk-level progress into both calls means a file whose placement falls back to a real copy (no hardlink support, or a blob's hard-link limit reached) reports its bytes twice: once for the source-read-into-store pass, once for the store-into-mirror copy pass. This is not a rare case - when the target filesystem doesn't support hardlinks at all (known upfront via `SupportsHardlinks`, probed before planning), *every* transferred file's placement takes the streamed-copy path, so every file's bytes would double-count for the entire run.

The two counters that matter are already independent, which keeps the fix local to progress display: `BackupExecutor`'s summary `counts.BytesTransferred += size` (feeding `ExecutionOutcome`/`SnapshotStats`, the audited run-summary figure) is driven directly by `StoreFromStream`'s returned `size`, once per file, and is untouched by how many times the new chunk-level progress callback fires. Only the *display* denominator - `plan.TotalBytesToTransfer`, used solely to compute the live percentage/ETA - needs adjusting. `BackupPipeline.Run` computes a separate progress-reporting total: `plan.TotalBytesToTransfer` when `contentStore.SupportsHardlinks` is true, or double that when false (every placement is known upfront to require a real copy in addition to the source read). `BackupPlan.TotalBytesToTransfer` itself is untouched, preserving its existing meaning ("total bytes of changed source content") for anything else that reads it. The rare remaining case - hardlinks supported at the volume level but a specific blob's per-file link limit reached - isn't knowable upfront and can still cause a small, transient, self-correcting overshoot past 100%; accepted as a bounded edge case rather than solved exactly.

Alternative considered: don't report progress from `PlaceAtMirrorPath`'s fallback copy at all, avoiding the double-counting question entirely. Rejected per proposal's explicit scope: on a non-hardlink target this would leave every file's mirror-placement copy invisible for its own duration, reintroducing the freeze this change exists to fix for the common exFAT/no-hardlink-target case.

## Risks / Trade-offs

- [More frequent progress callbacks (once per internal read chunk instead of once per file) add a small amount of overhead per file transfer] → Mitigated by keeping the callback itself trivial (an `Interlocked.Add` plus an `IProgress<T>.Report`, which is already designed to be cheap/non-blocking); no I/O or locking happens inside the hot path.
- [Wrapping streams changes the code path exercised for every file transfer, including small files] → Mitigated by covering small-file and empty-file cases explicitly in tests (existing behavior - final hash/size/dedup outcome - must be byte-for-byte identical to today, just produced via a different internal path).
- [`PlaceAtMirrorPath`'s manual streamed copy replaces `File.Copy`, which may have OS-level fast-path optimizations (e.g. copy offload) that a manual read/write loop loses] → Accepted trade-off per proposal's explicit scope decision to include this path; if a measurable regression surfaces during implementation, revisit by benchmarking manual-copy throughput against `File.Copy` on the target platform.
- [Removing the temp-file re-read changes the hash computation's data source from "re-read from disk" to "read from the in-flight tee"] → No behavior change expected since both ultimately hash identical bytes; still worth an explicit test asserting the resulting content hash for a given input is unchanged from before this change.
- [Doubling the progress-reporting total on non-hardlink targets makes the initial "total bytes to transfer" figure diverge from the literal sum of changed source file sizes] → Intentional trade-off (see "Avoiding double-counting" decision): the displayed total reflects expected local I/O work, keeping the live percentage bounded and meaningful, at the cost of it no longer equaling the run's audited `BytesTransferred` summary figure shown at completion (which is unaffected and still reflects true source content size).

## Migration Plan

Internal-only change with no persisted-data or CLI-surface impact - no migration steps or rollback plan beyond normal code review and the test suite (unit tests around `FileSystemContentStore`, `BackupExecutor`, and `BackupPipeline`, plus a scenario-level check that progress advances mid-transfer for a large file).
