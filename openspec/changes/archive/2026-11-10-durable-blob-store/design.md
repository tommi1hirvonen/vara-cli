## Context

See proposal.md - Why. What an implementer in a fresh session needs:

- `ContentStore.storeFromStream` (`src/Vara.Engine/Storage/ContentStore.fs`) reads the source once through a `TeeStream`, writing a staging file under `.vara/tmp` through a `BufferedStream` while `Hashing.hashStream` hashes it. It then names the blob `versions/<hash[0..2]>/<hash>`: if that file exists the staging file is deleted, otherwise it is moved into place. `Execution.transferContent` calls it, then `placeAtMirrorPath`.
- A blob is made read-only once a mirror file hard-links to it (`placeAtMirrorPath`, `ensureReadOnly`), so a blob is often read-only and shared with mirror files.
- `RestoreRun.writeFile` and `ConfigFile.DurableFiles.real` already flush before renaming: `sink.Flush()` then `FileStream.Flush true`. The store does not.
- The README states "crash-safe atomic writes" and "automatic recovery/cleanup on the next run"; `check` (`CheckRun`) is the only existing detector of damaged blobs.
- Tests: a module named after the requirement title, a test named after the scenario title (F# double-backtick names). `tests/Vara.Testing/SpecCoverage.fs` fails when a scenario of a main spec has no test, which applies once this change is synced. Use the exact titles in `specs/backup-execution/spec.md`.

## Goals / Non-Goals

**Goals:**
- A blob never takes its content name with data that is still only in the operating system's cache.
- A present blob whose size is wrong is repaired when the same content is stored again, for the blob and for every name linked to it.
- Parallel transfers of the same content are safe against a damaged blob: no failure and no mirror file copied from a half-rewritten blob.
- The flush call is observable in a test.

**Non-Goals:**
- Detecting damage in blobs nothing re-stores, or damage that keeps the size (bit rot). `vara check` does that; a size comparison is one stat call and catches truncation, which is what an unflushed rename produces.
- Making manifest commits stricter (backups keep `synchronous = NORMAL`; a lost recent commit only means files are detected again).
- Making mirror placement or directory entries durable.
- Re-reading existing blobs to verify them on every run.
- An explicit repair of blobs nothing re-stores (a `check` option). It would solve detection and repair together and is a separate change.
- Cross-process safety of the repair. `RunLock` already allows one writing run per target.

## Decisions

**Flush each staging file before it is renamed (per blob), as `RestoreRun.writeFile` already does.** After the tee has been consumed, call `sink.Flush()` and then the flush effect on the `FileStream` (`Flush true`, which maps to `FlushFileBuffers`), before the streams are disposed and before the rename or the existence check.
Alternatives considered: flushing once per manifest checkpoint rather than per blob (fewer flushes, but a blob is hard-linked and made read-only right after it is stored, so reopening it for write to flush would first have to clear the attribute, and the blob already has its final name by then, which is the very state to avoid); a volume flush (needs elevation); no flush plus a verify pass (moves the cost to a read of everything). Per blob is the only option that keeps "complete before it has its name" true.

**The flush is a function in `StoreLayout`, set by `layout` to the real flush.** A test replaces it with `{ layout with Flush = ... }` to assert that it ran while the blob did not yet exist. `StoreLayout` is created in one place and already carries behaviour-free context (it has just gained a lazy resolved root), so this adds no ripple through `ExecutionEnv`, `BackupEnv` or the benchmark fixtures. Alternative: add a parameter to `storeFromStream` (4 call sites plus every environment record that would have to carry it) or widen the `Links` record (its name stops being accurate, and the three places that build it change).

**Repair in place, not by renaming over the blob, under a lock per content name.** When the blob exists, compare its length with the size just read (`tee.TotalBytesCopied`). If they are equal, delete the staging file as today. If they differ, clear the read-only attribute (remembering whether it was set), open the existing blob with `FileMode.Create` (this truncates the same file, so every hard link to it sees the new content), copy the staging file's bytes into it, flush it through the same `Flush` function, delete the staging file, and restore the read-only attribute. A `finally` restores the attribute and a failure deletes the staging file and propagates as the file's I/O failure, so the file is reported as failed and the next store of that content repairs again. A rename over the blob would leave each existing mirror entry linked to the old damaged file, which the planner considers unchanged (same size and time), so it would stay damaged.
The check and the repair are one critical section per content name. Without it, two transfers of the same damaged content (two identical files in one run, which transfers in parallel) both see the wrong size and both open the blob for writing: one gets a sharing violation and fails, and in the no-hard-link mode `placeAtMirrorPath` copies from the blob, so a worker could copy a blob another worker has just truncated and write a short mirror file with no error. With the critical section every store of that content passes the size check after any repair has finished, and a worker that found the right size has no repair to wait for, so nothing reads a blob while it is rewritten. The existing "present" check and `File.Move` of a new blob need no lock: the move is atomic and its race is already handled by the `IOException` branch.
The locks are a fixed array of 256 objects in `StoreLayout`, created in `layout` and indexed by the blob's two-character directory name (`hash[0..2]`, the same fan-out the store uses). Alternatives considered: a dictionary of one lock per hash (exact, but grows by one entry per stored file in a run of millions); one lock for the whole store (serializes unrelated repairs, though repairs are rare enough that it would also be fine); a named OS mutex (cross-process, which `RunLock` already provides). The lock is taken only on the branch where the blob exists, so a store of new content never contends.
If the repair is interrupted, the blob is still of the wrong size and the next store of that content repairs it again; no new failure mode is introduced.

**Size, not hash, is the check.** One `FileInfo` per already-present blob, which the existence check already touches. Hashing every present blob would re-read the store on every transfer.

**Cost measurement before merge.** The per-file flush is the cost of this change. Record, in this file's Risks section, how long N small files (for example 1,000 files of 4 KiB) take to back up before and after, on an external drive of the kind the tool targets. The benchmark project (`benchmarks/`, see its README for the quiet-machine procedure) has `ContentStoreBufferBenchmarks` and transfer benchmarks that can be extended; allocations are the reliable numbers on a noisy machine, timings need the quiet-machine gate. The decision above stands unless the measurement shows an unacceptable slowdown, in which case stop and bring the numbers to the author instead of choosing a different cadence unilaterally.

## Risks / Trade-offs

- [A flush per stored file slows backups of many small new files, most on drives with slow flushes] → Measured as described; changed or unchanged files are not affected, only files whose content is stored.
- [The flush itself cannot be proven by a unit test] → The seam proves it is called before the name exists; the operating system's behaviour for `FlushFileBuffers` is not re-tested.
- [A size match does not prove the content] → Stated non-goal; `vara check` is the full verification.
- [In-place repair is not atomic] → The blob was already damaged; a later store repairs it again, and the lock keeps other transfers out while it runs.
- [Two content names that share a stripe wait for each other during a repair] → Repairs are rare and short, and the lock is not taken for new content.
- [The race fix cannot be proven by one run of a test] → The concurrency test repeats the parallel store many times against a fresh damaged blob each time; it fails on an implementation without the lock, though not on every iteration.

### Measured cost

1,000 new files of 4 KiB through `storeFromStream` on the internal system drive (no external drive was available), release build, "before" taken by replacing `Flush` with a no-op through the seam, three alternating rounds. Timings are noisy on this machine (Defender); allocations are stable.

| Variant | Time per 1,000 files | Allocated |
| --- | --- | --- |
| Without flush | 2680, 2026, 2291 ms | 84.6 MiB |
| With flush | 2519, 3064, 2757 ms | 84.6 MiB |

The flush adds no allocations. The timing difference is within the noise of this machine, roughly 0 to 1 ms per stored file on an SSD. The cost on an external drive is not measured; <insufficient data> for that case, and the quiet-machine gate on the target hardware remains for the author.

