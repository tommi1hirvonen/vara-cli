## 1. Streamed copy+hash primitive

- [ ] 1.1 Add a tee-style stream helper (in `Vara.Core`, alongside `StreamingContentSignature`) that wraps a source `Stream` and a destination `Stream`, writes each read chunk to the destination as it is read, and invokes an `Action<long>` callback with each chunk's size; verify with a unit test that content read through it matches the source exactly and that the destination stream ends up byte-for-byte identical to the source
- [ ] 1.2 Verify the tee helper composes correctly with a `BufferedStream` wrapped around the source (to keep upstream disk reads large even if the consumer requests small chunks), via a unit test asserting no data loss/reordering across a range of buffer and destination-chunk-size combinations

## 2. Content store: single-pass copy+hash

- [ ] 2.1 Add an optional `Action<long>? onBytesWritten` parameter to `IContentStore.StoreFromStream`, defaulting to `null` (non-breaking)
- [ ] 2.2 Rework `FileSystemContentStore.StoreFromStream` to drive the temp-file write and the content hash from a single pass over the source (via the tee helper from 1.1 and `_hasher.ComputeHash`), removing the existing separate `CopyTo` + temp-file-reopen-and-hash steps; verify existing `FileSystemContentStoreTests` still pass (hash/size/dedup behavior unchanged) and add a test asserting `onBytesWritten` is invoked multiple times with increasing cumulative totals for a file larger than one internal read chunk
- [ ] 2.3 Replace both `File.Copy` call sites in `FileSystemContentStore.PlaceAtMirrorPath` (no-hardlink-support path, and hardlink-limit-reached fallback) with a streamed copy using the same tee helper and an optional progress callback parameter added to `PlaceAtMirrorPath`; verify existing hardlink-fallback tests still pass and add a test asserting progress is reported incrementally during the fallback copy

## 3. Executor and pipeline wiring

- [ ] 3.1 Update `BackupExecutor.ExecuteTransfer` to pass a per-file progress callback into `StoreFromStream` (and into `PlaceAtMirrorPath`) that forwards chunk deltas to `onBytesTransferred` directly, instead of invoking `onBytesTransferred` once after the file completes; keep the existing `reportLock`-guarded manifest write and `counts.BytesTransferred` summary increment as a single once-per-file update, unchanged; verify with a `BackupExecutorTests` test that `onBytesTransferred` is invoked multiple times for a single large-file transfer, with a final cumulative total equal to the file's size
- [ ] 3.2 Change `BackupPipeline.Run`'s `bytesSoFar` accumulation from a captured-local `+=` to an `Interlocked`-safe counter (e.g. a boxed `long` field updated via `Interlocked.Add`), independent of `BackupExecutor`'s per-file `reportLock`; verify with a `BackupPipelineTests` test that concurrent chunk-level callbacks (e.g. from a configured `transfer_concurrency` > 1 across multiple files) never lose an update and the final reported total equals the sum of all files' sizes

## 4. End-to-end verification

- [ ] 4.1 Add an integration-level test (e.g. in `Vara.IntegrationTests`) that runs a backup of a single large file (large enough to span many internal read chunks) with a captured `IProgress<BackupProgress>`, and verify more than one progress report is received with strictly non-decreasing, increasing intermediate `BytesTransferred` values before the final report equal to the file's size
- [ ] 4.2 Run the full test suite (`dotnet test`) and verify all tests pass, including the existing `progress-reporting`, `backup-execution`, and content-store test coverage
