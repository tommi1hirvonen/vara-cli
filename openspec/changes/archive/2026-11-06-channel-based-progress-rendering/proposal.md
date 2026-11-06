## Why

The backup command's live progress display coordinates rendering across threads with four separate mechanisms: `TrackedProgress<T>` dispatches every report onto the thread pool and counts outstanding deliveries, `CoalescedActivePathRefreshDispatcher` runs its own worker guarded by two locks and `Monitor.Wait`/`PulseAll`, `BackupCommand.RunWithLiveDisplay` serializes Spectre mutations behind a `displaySync` lock plus a `displayOpen` flag, and teardown composes two separate bounded drains. Each mechanism exists for the same reason - several threads touching one Spectre `ProgressContext` - and their interaction is the kind of hand-written lock and condition-variable code where ordering, teardown, and fault-propagation bugs hide. A single `System.Threading.Channels` channel drained by one render loop expresses the same behavior with one owner of the display, no per-report thread-pool fan-out, and one drain.

## What Changes

- Introduce one internal render loop in `Vara.Cli/Presentation` that accepts backup progress reports and active-path refresh requests from any thread through a single unbounded, single-reader channel, and invokes the render callbacks from one consumer only.
- Preserve the existing coalescing contract for active-path refreshes: the consumer evaluates only the latest pending capture function per drained batch, so refresh requests can never build an unbounded backlog and each rendered list reflects the in-flight state at capture time.
- Preserve exact numeric-statistics semantics by passing every progress report, in channel order, through the existing `ProgressDisplayGate` (rate limit, first/final always rendered, never-regress guard) - progress reports themselves are not collapsed.
- Replace the composed teardown (`TrackedProgress.TryDrain` + dispatcher `TryDrain` + `ThrowIfFaulted` + `Close` + `CloseDisplay`) with a single bounded close that stops intake, drains, runs the final outcome stamping exclusively, and guarantees no render callback runs afterward - even when the drain bound is exceeded.
- Surface a render-callback failure to the command after the run (as the dispatcher does today for active-path refreshes) instead of crashing the process from a thread-pool work item, and stop accepting further events once faulted so a failed renderer cannot accumulate an unbounded backlog.
- Route the non-interactive (plain output) backup path through the same loop instead of `Progress<T>`, so plain progress lines are delivered in order and are fully drained before the run summary is printed.
- Remove `TrackedProgress<T>` and `CoalescedActivePathRefreshDispatcher` and replace their unit tests with equivalent tests for the render loop.
- Restore, check, and prune displays are unchanged: they already report inline on a single thread and gain nothing from a channel.

## Capabilities

### New Capabilities
<!-- None: pure internal refactoring of the CLI's progress delivery mechanism. -->

### Modified Capabilities
<!-- None: all progress-reporting requirements (rate limiting, never-regress, coalesced active-path refresh, delivery-before-teardown, outcome coloring) are preserved unchanged. skip_specs: true is set. -->

## Impact

- **Code**: `src/Vara.Cli/Commands/BackupCommand.cs` (`RunWithLiveDisplay`, `RunWithPlainOutput`); new render loop in `src/Vara.Cli/Presentation`; removal of `src/Vara.Cli/Presentation/TrackedProgress.cs` and `src/Vara.Cli/Presentation/CoalescedActivePathRefreshDispatcher.cs`; doc-comment updates in `src/Vara.Application/Reporting/ProgressDisplayGate.cs` that describe thread-pool delivery.
- **Tests**: `tests/Vara.Cli.Tests/Presentation/TrackedProgressTests.cs` and `CoalescedActivePathRefreshDispatcherTests.cs` replaced by render-loop tests; existing `BackupCommandTests` and `ProgressDisplayGateTests` must pass unchanged.
- **Contracts**: No change to `BackupPipeline.Run`'s signature or to the `IProgress<BackupProgress>` / active-path refresh callback contract it exposes, so this change is independent of `channel-based-backup-recording`.
- **Dependencies**: `System.Threading.Channels` is part of the shared framework; no new package references. Native AOT compatible.
