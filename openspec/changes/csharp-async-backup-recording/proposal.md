## Why

`main` already funnels per-file transfer results through a channel to one recorder (`channel-based-backup-recording`), and render callbacks through one consumer (`channel-based-progress-rendering`). Several pieces still rely on shared-memory synchronization or blocking:

- The recorder **blocks** on the channel (`WaitToReadAsync(...).AsTask().GetAwaiter().GetResult()`), and waits for the workers with `transferTask.Wait(...)`.
- Byte, file-started and file-finished callbacks still run **on worker threads**. That is why `BackupPipeline` needs `Interlocked`, `Volatile` and a `ConcurrentDictionary`, and why progress reports can reach the display gate out of order.
- The render loop's teardown needs a `Lock`, two `Volatile` fields and a blocking `Task.Wait` to guarantee that no callback runs after `Close`.
- The move-detection hasher writes into a `ConcurrentDictionary` from `Parallel.ForEach`.

The cleaner shape:

- every mutable piece of run state has one owner;
- workers only emit immutable events;
- the run awaits instead of blocking;
- every background task is joined before its owner returns.

`async`/`await`, `Channel<T>` and `Parallel.ForEachAsync` express this directly. A probe confirmed that `Parallel.ForEachAsync` with a blocking synchronous body reaches full concurrency, and that an awaiting single reader never overlaps itself.

This builds on `csharp-closed-set-redesigns` (the `TransferOperation`/`MetadataOperation` sub-bases) and must be applied after it.

## What Changes

- **Async boundary at the entry point:** `Program.cs` awaits `parseResult.InvokeAsync(new InvocationConfiguration { ProcessTerminationTimeout = null })`, so System.CommandLine installs no Ctrl+C handler of its own and Vara's `GracefulCancellation` stays in charge. Only the `backup` command's action becomes `async`; every other command keeps its synchronous action.
- **`BackupExecutor.ExecuteAsync`:** workers run in `Parallel.ForEachAsync` and publish a `closed record TransferEvent` hierarchy (`FileStarted`, `BytesCopied`, `FileFinished`, `Transferred`, `Failed`, `Crashed`). The method body awaits the channel as the single recorder and invokes every callback itself, sequentially. The pool is always awaited before the method completes. The blocking receive, `Task.Wait`, `GetAwaiter().GetResult()` and the abort catch block are removed. `TransferResult` is replaced.
- **`BackupPipeline.RunAsync`:** progress counters and the in-flight set become plain locals owned by the recorder. `requestActivePathsRefresh(Func<...>)` becomes `onActivePathsChanged(IReadOnlyList<string>)`. `PlanOnly` stays synchronous.
- **`BackupPlanner`:** move-detection hashing keeps `Parallel.ForEach` but writes to a per-index result array instead of a `ConcurrentDictionary`. `Plan` stays synchronous.
- **Rendering:** `BackupProgressRenderLoop` (a class with a `Lock`, `Volatile` fields and a blocking `Close`) is replaced by `BackupProgressRendering.RunAsync`. A consumer task drains a `closed record DisplayEvent` channel, the run is awaited, the consumer is awaited with the drain timeout, and the outcome is stamped after the consumer has finished, so no lock is needed. `LiveProgress` gets a `RunAsync<T>` over Spectre's `StartAsync`.
- **Error reporting and power request:** both get async overloads (`ErrorReporting.RunAsync`, `RunWithPowerRequestAsync`).
- **Recorder thread invariant:** "on the calling thread" becomes "one at a time, never concurrently", because the recorder resumes on thread-pool threads between awaits. The thread-affinity test is replaced by an overlap test.

**BREAKING (internal API only):** `BackupExecutor.Execute` → `ExecuteAsync`, `BackupPipeline.Run` → `RunAsync`, `BackupProgressRenderLoop` removed. None of these are user-facing; CLI behavior, output, exit codes and on-disk formats are unchanged.

## Capabilities

### New Capabilities

None.

### Modified Capabilities

None. This is a behavior-preserving refactor (`skip_specs: true`). The `backup-execution` and `progress-reporting` requirements (graceful cancellation, crash/interruption safety, delivery before display teardown, outcome color, rate limiting and never-regress) are kept as they are. Making progress reports globally ordered is an improvement *within* those requirements; the gate's never-regress guard stays.

## Impact

- **Code:**
  - `src/Vara.Cli/Program.cs`;
  - `src/Vara.Cli/Commands/BackupCommand.cs`;
  - `src/Vara.Cli/Composition/ErrorReporting.cs`;
  - `src/Vara.Cli/Presentation/{BackupProgressRenderLoop → BackupProgressRendering, LiveProgress}.cs`;
  - `src/Vara.Application/Backup/{BackupExecutor,BackupPipeline,BackupPlanner}.cs`;
  - the doc comment of `src/Vara.Application/Reporting/ProgressDisplayGate.cs`.
- **Tests:**
  - `BackupExecutorTests` (48 call sites), `BackupPipelineTests` and the IntegrationTests that call `Execute`/`Run` become `async Task` tests.
  - `BackupProgressRenderLoopTests` is rewritten as `BackupProgressRenderingTests`.
  - `All_repository_and_manifest_batch_calls_happen_on_the_calling_thread_with_concurrent_transfers` is replaced by an overlap test.
- **Benchmarks:** `BackupTransferBenchmarks` (executor and pipeline) return `Task`. They are compared against the base-commit baseline (see `csharp-closed-set-redesigns` task 1.1), with the I/O tolerance.
- **Dependencies:** none. System.CommandLine 2.0.11 and Spectre.Console 0.57.2 already provide the APIs (verified by probe).
