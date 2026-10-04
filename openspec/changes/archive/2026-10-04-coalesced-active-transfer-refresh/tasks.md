## 1. Separate Progress State and Admission

- [x] 1.1 Update numeric progress admission so only the initial update, the final update, or a report after the 100 ms interval redraws numeric statistics; verify gate tests cover rapid file-count changes and monotonic counters.
- [x] 1.2 Add a lifecycle-triggered active-path refresh signal that captures the current in-flight paths when serviced instead of replaying queued path snapshots; verify start, finish, failure cleanup, and out-of-order notification tests.

## 2. Coalesce Live Display Refreshes

- [x] 2.1 Keep the latest admitted numeric statistics separate from active paths in the interactive display, and implement a bounded latest-wins refresh dispatcher that does not block transfer workers; verify bursts coalesce, active paths refresh independently, and numeric fields remain unchanged between admitted updates.
- [x] 2.2 Preserve progress delivery draining and teardown behavior for queued/coalesced refreshes; verify no callback can update the live display after teardown and existing plain-output behavior is unchanged.

## 3. Validate Active Transfer Behavior

- [x] 3.1 Add deterministic coverage showing that a short transfer may be omitted when start and finish coalesce, while a path completed before state capture is never shown as active; verify the progress-reporting and CLI test projects pass.
- [x] 3.2 Run the focused Application and CLI test suites sequentially and confirm the active-path display remains responsive without changing the numeric-stat refresh cadence.
