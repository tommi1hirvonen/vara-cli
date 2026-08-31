## 1. Progress display gate

- [ ] 1.1 Add a `ProgressDisplayGate` (or similarly named) class to `Vara.Application.Reporting`, alongside `BackupProgressCalculator`, that: tracks the highest `BytesTransferred` value rendered so far and discards any incoming value lower than that maximum; tracks the wall-clock time of the last render via an injectable clock (same `nowProvider` pattern as `BackupProgressCalculator`) and suppresses redraws more frequent than a fixed 100ms interval; always allows the first report (`BytesTransferred == 0`) and the final report (`BytesTransferred == TotalBytes`) through regardless of the throttle; performs its decision and the resulting render callback under a single internal lock. Verify the class builds and is unit-testable in isolation (no `Console` dependency baked in - accept a render delegate/callback).
- [ ] 1.2 Wire `BackupCommand`'s `Progress<BackupProgress>` handler to route through the gate (compute the `ProgressSnapshot` via `BackupProgressCalculator` only when the gate admits the report, then `Console.Write` the formatted line) instead of writing unconditionally on every report. Verify by inspecting the updated `BackupCommand.cs` and confirming the existing backup command still builds and runs against a sample profile.

## 2. Tests

- [ ] 2.1 Add unit tests in `Vara.Application.Tests/Reporting` covering the monotonic guard: reports delivered out of order (e.g. 500 then 300) result in only the highest value being admitted/rendered, and never a regression to a lower value. Verify via `dotnet test --filter` on the new test class passing.
- [ ] 2.2 Add unit tests covering the throttle: multiple reports fired within the 100ms window are collapsed to a single render using an injected fake clock; a report after the interval elapses is admitted; the first report (0 bytes) and the completion report (`BytesTransferred == TotalBytes`) are always admitted regardless of elapsed time since the last render. Verify via the same test run passing.

## 3. Verification

- [ ] 3.1 Run the full test suite (`dotnet test`) and confirm all tests pass, including the new `ProgressDisplayGate` tests and existing `BackupProgressCalculatorTests`/`BackupCommand`-related tests.
- [ ] 3.2 Manually run `vara backup` against a profile with many small changed files and confirm the progress line updates at a readable, bounded rate and never visibly jumps backward.
