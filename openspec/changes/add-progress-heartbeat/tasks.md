## 1. `ProgressHeartbeat` component

- [x] 1.1 Add `Vara.Application.Reporting.ProgressHeartbeat` with an `Update(BackupProgress)` method that remembers the most recent value, and a `Tick(Action<BackupProgress> render)` method that invokes `render` with that last-known value - a no-op if `Update` has never been called; verify with a unit test that `Tick` before any `Update` does not invoke `render`, and that it invokes `render` with the exact last-updated value afterward
- [x] 1.2 Verify `Tick` is safe to call from a different thread than `Update` (mirroring how a real timer callback runs on a thread-pool thread while transfer threads call `Update`), via a unit test that calls both concurrently without throwing or losing the most recent value
- [x] 1.3 Verify composing `ProgressHeartbeat.Tick` with a real `ProgressDisplayGate` reproduces the intended effect end-to-end: with a controllable `nowProvider`, report an initial value, advance time past the gate's interval without any further `Update`, call `Tick`, and confirm the gate admits and renders the repeated byte value (so `BackupProgressCalculator.Calculate`, if wired in, would recompute throughput/ETA against the new elapsed time)

## 2. Wiring into `BackupCommand`

- [x] 2.1 In `BackupCommand.Create`, construct a `ProgressHeartbeat`, call its `Update` from the existing `Progress<BackupProgress>` callback (alongside the existing `displayGate.Report` call), and start a `System.Threading.Timer` on a fixed interval (comfortably larger than `ProgressDisplayGate`'s redraw interval) that calls `Tick` with the same `displayGate.Report(..., render)` callback already used for real reports
- [x] 2.2 Dispose the timer once `pipeline.Run(...)` returns (e.g. via `using`), so no further heartbeat ticks occur after the run completes; verify by manual/CLI smoke test (`vara backup <profile>`) that the progress line still updates and stops cleanly at completion without a trailing stray redraw after the summary is printed

## 3. End-to-end verification

- [x] 3.1 Add a test simulating a stall: using injected `nowProvider`s for `ProgressDisplayGate` and `BackupProgressCalculator`, report one progress value, advance simulated time well past the run's elapsed baseline with no further `Update`, invoke `Tick`, and verify the resulting `ProgressSnapshot`'s throughput is lower and ETA is longer than it was at the moment of the last real report - confirming the heartbeat causes the displayed metrics to reflect the stall rather than staying frozen
- [x] 3.2 Run the full test suite (`dotnet test`) and verify all tests pass, including the existing `progress-reporting` capability's test coverage
