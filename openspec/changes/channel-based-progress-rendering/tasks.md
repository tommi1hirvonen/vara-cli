## 1. Render Loop

- [x] 1.1 Create `src/Vara.Cli/Presentation/BackupProgressRenderLoop.cs`: an internal sealed class implementing `IProgress<BackupProgress>` and `RequestActivePathsRefresh(Func<IReadOnlyList<string>>)`, backed by one unbounded single-reader `Channel<DisplayEvent>` drained by one background consumer task (design.md Decisions 1 and 3). Verify `dotnet build Vara.slnx` succeeds.
- [x] 1.2 Implement batch draining in the consumer: every `ProgressReported` event goes through `ProgressDisplayGate.Report` in channel order, and only the latest `ActivePathsRequested` capture per batch is evaluated on the consumer and passed to the refresh callback (Decision 2). Verify with new unit tests in `tests/Vara.Cli.Tests/Presentation/BackupProgressRenderLoopTests.cs`: reports from one producer render in order, a burst of refresh requests evaluates the latest capture once, and the capture runs on the consumer, not the producer.
- [x] 1.3 Implement `Close(TimeSpan drainTimeout, Action? finalRender)` and the consumer-side `closed` check under a single `Lock` (Decision 4). Verify with unit tests: Close waits for a delayed final report up to the bound; Close returns false without hanging when a callback blocks past the bound; `finalRender` runs exactly once; and no render callback runs after Close returns, including after a timeout.
- [x] 1.4 Implement fault capture: a throwing callback completes the writer with the exception, later `Report`/refresh calls are ignored, and `ThrowIfFaulted()` rethrows the original exception (Decision 5). Verify with unit tests covering both a throwing progress render and a throwing active-path refresh.

## 2. Backup Command Integration

- [x] 2.1 Rewire `BackupCommand.RunWithLiveDisplay` to use the render loop: drop `TrackedProgress`, `CoalescedActivePathRefreshDispatcher`, `displaySync`, `displayOpen` and `DrainCallbacks`; route outcome stamping through `Close(drainTimeout, finalRender)` on both the success and the exception path; call `ThrowIfFaulted()` after `Close` on success. Verify that `dotnet test tests/Vara.Cli.Tests --filter "FullyQualifiedName~BackupCommand"` passes unchanged.
- [x] 2.2 Rewire `BackupCommand.RunWithPlainOutput` to use the render loop without an active-path callback instead of `Progress<BackupProgress>`, closing it before returning (Decision 6). Verify the plain-output backup tests pass and that no progress line is written after the method returns, either with a new test or an assertion added to an existing plain-output test.

## 3. Cleanup

- [x] 3.1 Delete `src/Vara.Cli/Presentation/TrackedProgress.cs`, `src/Vara.Cli/Presentation/CoalescedActivePathRefreshDispatcher.cs`, `tests/Vara.Cli.Tests/Presentation/TrackedProgressTests.cs` and `tests/Vara.Cli.Tests/Presentation/CoalescedActivePathRefreshDispatcherTests.cs`, after confirming each of their scenarios has an equivalent in `BackupProgressRenderLoopTests`. Verify that grep finds no remaining references and that the solution builds.
- [x] 3.2 Update doc comments that describe thread-pool `Progress<T>` delivery (`ProgressDisplayGate`, the comments in `BackupCommand`) so they describe the render loop and the remaining cross-worker reordering the gate guards against (design.md Risks). Verify by reading the diff for accuracy.

## 4. Verification

- [x] 4.1 Run the full test suite with `dotnet test Vara.slnx` and verify all tests pass.
- [x] 4.2 Publish the CLI with Native AOT (`dotnet publish src/Vara.Cli -c Release`) and verify it introduces no new trim/AOT warnings and still yields a single standalone `vara.exe` (`portable-distribution`).
- [x] 4.3 Manually run an interactive backup with `transfer_concurrency` above 1 against a sample profile, then a redirected (plain) backup. Verify that the scan spinner swaps to the bar/stats rows, that active paths update and clear, that the final bar color matches the outcome, that Ctrl+C still stops gracefully, and that plain progress lines never appear after the run summary.
