## Context

See proposal.md - Why. The archived designs of `channel-based-backup-recording` and `channel-based-progress-rendering` define the failure, cancellation and teardown contracts this change preserves.

Facts verified with throwaway probes while planning (.NET SDK 11.0.100-rc.1, System.CommandLine 2.0.11, Spectre.Console 0.57.2):

- `Parallel.ForEachAsync` with `MaxDegreeOfParallelism = 4` and a **synchronous, blocking** body (returning `ValueTask.CompletedTask`) reached four concurrent bodies in 40 of 40 runs. With the default `AllowSynchronousContinuations = false`, a channel write never runs the reader's continuation inline on the writer's thread, so the recorder's synchronous SQLite writes never stall a worker.
- A recorder that does `await foreach` over `ReadAllAsync()` with synchronous work per item never overlapped itself (0 overlaps in 40 runs) while the pool kept writing.
- Top-level `await root.Parse(args).InvokeAsync(new InvocationConfiguration { ProcessTerminationTimeout = null })` propagates an async action's exit code. `SetAction(async (ParseResult, CancellationToken) => ...)` binds the async overload.
- `AnsiConsole.Progress().StartAsync(async ctx => ...)` returns the body's `T`.
- `Microsoft.Data.Sqlite` connections are not thread-affine. They may be used from different threads one after another, but never concurrently, so a recorder that resumes on varying pool threads is safe as long as it never overlaps itself.

## Goals / Non-Goals

**Goals:**
- Every piece of mutable state touched during a backup run has exactly one owner at a time. No `Interlocked`, `Volatile`, `Lock`/`lock`, `ConcurrentDictionary`, `ManualResetEventSlim`, `Task.Wait` or `GetAwaiter().GetResult()` remains in `BackupExecutor`, `BackupPipeline`, `BackupPlanner` or the backup rendering.
- Every background task started during a run is awaited before the method that started it completes, on success, cancellation and failure paths alike.
- Nothing in `src` blocks on a task.

**Non-Goals:**
- The `Lock`s in `ProgressDisplayGate` and `WindowsTerminalProgress`. Each guards a tiny critical section, and both are also used by the synchronous commands. Only the gate's doc comment, which says concurrent workers call it, is corrected. After this change, backup reports reach it from one consumer in a global order.
- Async I/O. Vara does local disk I/O through synchronous SQLite, YAML and file APIs. Workers stay synchronous inside `Parallel.ForEachAsync`, and only coordination is async.
- Restore, check, prune, dry run and the other commands. They stay synchronous; `PlanOnly` and `BackupPlanner.Plan` stay synchronous too.
- Replacing `Parallel.ForEach` in the planner. It is a pure fork-join over independent items, which is the job `Parallel.ForEach` exists for.

## Decisions

### 1. The async boundary is the entry point; only `backup` is async

- `Program.cs` ends with `return await parseResult.InvokeAsync(new InvocationConfiguration { ProcessTerminationTimeout = null });`. A `null` timeout stops System.CommandLine from registering its own Ctrl+C handler, which would cancel its token and terminate the process after a timeout, breaking the "in-flight transfers finish" and "second Ctrl+C forces exit" requirements. The `Console.CancelKeyPress` wiring and `GracefulCancellation` are unchanged.
- The backup command's action becomes `SetAction(async (parseResult, _) => ...)`. It ignores System.CommandLine's token and keeps using Vara's graceful token.
- `ErrorReporting.RunAsync(Func<Task>)` mirrors `Run(Action)`, with the same catch blocks. The two share one private exception-to-exit-code mapping, so the handling cannot drift. `RunWithPowerRequest` becomes `RunWithPowerRequestAsync(Func<Task>)`.
- Synchronous actions of other commands keep working under `InvokeAsync`.

*Alternative:* keep `Invoke()` and block inside the backup action (`GetAwaiter().GetResult()`). Rejected: it keeps a sync-over-async bridge, which is exactly what this change removes, and it would only move the blocking up one level.

### 2. `ExecuteAsync`: the method body is the single recorder

```text
ExecuteAsync
  metadata pass over plan.Operations.OfType<MetadataOperation>()   (sequential, synchronous, unchanged)
  onTransferPhaseStarting()
  events = Channel.CreateUnbounded<TransferEvent>(SingleReader = true)
  pool = RunPoolAsync(...)         // Parallel.ForEachAsync; completes the writer in finally; never throws
  try   { await foreach (var e in events.Reader.ReadAllAsync()) Record(e); }
  finally { stopTransfers.Cancel(); await pool; }
  if (crashes.Count > 0) throw new AggregateException(crashes);
```

- **Workers:** `Parallel.ForEachAsync(transferOperations, new ParallelOptions { MaxDegreeOfParallelism = degree }, body)`. The body is synchronous and returns `ValueTask.CompletedTask`. It first checks `shouldStop()` (the graceful Ctrl+C token **or** `stopTransfers`) and skips the item if it is set. Neither token is passed as `ParallelOptions.CancellationToken`, because that would throw `OperationCanceledException` and could abandon queued bookkeeping. Each skipped item costs one flag check.
- **Events:** every worker callback becomes an event. The content store's per-chunk callback writes `BytesCopied`, and `FileStarted`/`FileFinished` bracket the transfer, with `onFileFinished` still emitted before the outcome (the archived Decision 4). The outcome is `Transferred`, `Failed`, or `Crashed(path, exception)` for an unexpected exception. Workers write with `TryWrite`, which always succeeds on an open unbounded channel, so they never wait on the recorder. Per-worker FIFO order guarantees that a path's start precedes its finish.
- **Recorder:** `Record(TransferEvent)` is a switch over the hierarchy. It invokes `onFileStarted`, `onBytesTransferred`, `onFileFinished` and `onFileTransferred`, writes the manifest, updates the counters and checkpoints. All of that happens on the recorder, sequentially, in channel order. A recorder that would need more than a few parameters is extracted into a small private `TransferRecorder` class that owns the counts, the failed paths, the crashes and the checkpoint clock, rather than threading all of them through method parameters.
- **Worker crash:** `Crashed` makes the recorder cancel `stopTransfers` and keep draining. Everything already produced is still recorded. After the drain, the crashes are thrown as `AggregateException`, which keeps today's exception shape.
- **Recorder failure:** the `finally` cancels `stopTransfers` and awaits the pool. In-flight transfers finish their blocking copy, so no worker still writes to the content store when the pipeline records the failure. The recorder's own exception then propagates unwrapped, as today (archived Decision 5). The pool never throws, because crashes are values, so the `finally` cannot mask the original exception.
- **Graceful Ctrl+C:** the body stops starting new items, in-flight ones finish and publish, the writer completes, and the recorder drains everything before `ExecuteAsync` returns (archived Decision 6).
- **Thread invariant:** the recorder resumes on thread-pool threads between awaits. The invariant becomes "one recorder at a time, never concurrently, never on a worker's thread".

*Alternatives:*
- Keep `Task.Run(() => Parallel.ForEach(...))`. It works, but `ForEachAsync` returns the task directly and keeps the whole pool in one awaited expression.
- A hand-written pool of `degree` `Task.Run` workers over a job channel. Rejected: it re-implements `Parallel.ForEachAsync`.
- Keep `onBytesTransferred` firing directly on workers. Rejected: it is the reason the pipeline needs atomics.

### 3. `BackupPipeline.RunAsync` owns plain progress state

- `bytesSoFar` and `filesSoFar` are plain locals, and the in-flight set is a plain `HashSet<string>(StringComparer.OrdinalIgnoreCase)`. They are only touched from executor callbacks, which now all run on the recorder.
- `requestActivePathsRefresh(Action<Func<IReadOnlyList<string>>>)` becomes `onActivePathsChanged(Action<IReadOnlyList<string>>)`, called with a fresh array after each start or finish. That costs one array per file lifecycle event, where today an array is built on every progress report.
- The run lock, reconciliation, the batch, checkpoints, the cancel/complete/fail recording and the `_diagnostics` path are unchanged. `using (runLock)` and the `try/catch` around the awaited executor keep their semantics.

### 4. The planner writes per-index slots

`ComputeMoveCandidateFullHashes` keeps `Parallel.ForEach` over the size-matched `AddedFile`s. It writes each result into `results[i]` of a `string?[]`, where each index has exactly one writer, and then builds the case-insensitive `Dictionary<string, string?>` serially. `Plan` stays synchronous. The tie-break and scan-concurrency tests must still pass unchanged.

*Alternative:* a worker pool feeding a channel that one consumer collects. Rejected: it would make `Plan` and `PlanOnly` async for no benefit; the fork-join has no ordering or ownership problem once slots are disjoint.

### 5. `BackupProgressRendering.RunAsync` replaces the render loop class

```text
RunAsync(gate, render, refreshActivePaths?, drainTimeout, produce, outcomeOf, stampOutcome?)
  events = Channel.CreateUnbounded<DisplayEvent>(SingleReader = true)
  consumer = Task.Run(() => ConsumeAsync(events.Reader, stop.Token, ...))   // returns Exception?, never throws
  try   { result = await produce(progress, onActivePathsChanged) }
  catch { runFailure = ExceptionDispatchInfo.Capture(e) }
  finally { events.Writer.Complete() }
  try   { fault = await consumer.WaitAsync(drainTimeout) }
  catch (TimeoutException) { stop.Cancel(); fault = await consumer }
  stamp outcome (Error on run failure or render fault, else outcomeOf(result))   // consumer has finished: no lock
  rethrow runFailure, else the render fault, with original stacks
```

- `DisplayEvent` becomes `closed record DisplayEvent` with `ProgressReported(BackupProgress)` and `ActivePathsChanged(IReadOnlyList<string>)`. The current struct with two nullable fields is deleted.
- **Consumer:** for each drained batch, every `ProgressReported` goes through the gate in order, and only the batch's latest `ActivePathsChanged` is rendered (coalescing, as today). It checks `stop` between callbacks. On a callback exception it stops and returns the exception, and it never throws. A render fault never cancels the backup.
- **Delivery before teardown:** no callback can run after `RunAsync` returns, because the consumer has completed. After the deadline the remaining queued events are dropped.
- **Outcome stamping** happens after the consumer has finished, on both the success and the failure path, and also after a drain timeout. That fixes today's timeout path, which skips stamping when the consumer is inside a callback.
- The live path (`LiveProgress.RunAsync`) and the plain-output path (no active paths) both use it. `--json` passes no progress, as today.

*Alternatives:*
- Keep the class and make only `Close` async. Rejected: it keeps the lock and the `Volatile` flags, because `Close` and the consumer would still race.
- Bounded teardown, where the run returns even if one callback never returns. Rejected: awaiting the consumer is what removes the lock. A stuck console write would block every later console write anyway, so nothing is gained by abandoning the consumer.

### 6. `LiveProgress.RunAsync<T>`

`LiveProgress.RunAsync<T>(console, columns, Func<ProgressContext, WindowsTerminalProgress?, Task<T>> body, startIndeterminate)` wraps `Progress().StartAsync`. It creates and disposes the terminal progress exactly like `Run`. The live backup display uses it; the dry run, restore, check and prune keep `Run`.

### 7. Tests and benchmarks

- Tests that call `ExecuteAsync`, `RunAsync` or the rendering become `public async Task` and await. Exception assertions use `await Assert.ThrowsAsync<T>(...)`. Test names stay, except where noted below.
- `All_repository_and_manifest_batch_calls_happen_on_the_calling_thread_with_concurrent_transfers` is replaced by `Repository_and_manifest_batch_calls_never_overlap_with_concurrent_transfers`. With `transfer_concurrency` 4 and slow fake transfers, the fake repository counts overlapping `RecordFileVersion`/`Commit` entries and asserts there are none.
- A new executor test asserts that every callback runs sequentially: an overlap counter in `onBytesTransferred`, `onFileStarted` and `onFileFinished` stays at 1, with concurrency 4.
- `BackupProgressRenderLoopTests` becomes `BackupProgressRenderingTests` and keeps every behavior it covers: ordering, coalescing, fault capture and rethrow, drop after deadline, no callback after return, and outcome stamping on success, failure and timeout.
- `BackupTransferBenchmarks` methods return `Task<T>`, which BenchmarkDotNet awaits.

## Risks / Trade-offs

- [The recorder resumes on different pool threads, and SQLite must not be used concurrently] → Only concurrency matters, not thread identity. Covered by the overlap test, the real-repository IntegrationTests and a smoke run with `transfer_concurrency: 4`.
- [System.CommandLine's handler intercepts Ctrl+C] → Verify on a real process. Start `vara backup` as a child with `CREATE_NEW_PROCESS_GROUP` (for example from Python's `subprocess`) and send `CTRL_BREAK_EVENT`, which reaches `Console.CancelKeyPress`. One break gives status Cancelled, exit code 2 and a consistent store; two breaks force an exit, and the next run reconciles the snapshot as Failed.
- [Per-chunk `BytesCopied` events add channel traffic] → About one 24-byte event per 80 KiB chunk on an unbounded channel, which is negligible next to the chunk's I/O. The executor and pipeline benchmarks are compared with the +30% I/O tolerance.
- [A render callback that never returns makes teardown wait] → Accepted (Decision 5).
- [A concurrency test becomes flaky after the change] → Run `Configuring_transfer_concurrency_above_one_allows_concurrent_transfers`, `Graceful_cancellation_with_concurrent_transfers_records_every_started_transfer_and_starts_no_more`, the new overlap test and `Unconfigured_scan_concurrency_permits_more_than_one_concurrent_hash_call` 40 times each.

## Migration Plan

One commit per task group, each green on `dotnet test Vara.slnx`. On-disk formats, CLI output and exit codes do not change. Rollback is a revert of the change's commits; it is independent of `csharp-closed-set-redesigns`, except that it uses that change's operation sub-bases.
