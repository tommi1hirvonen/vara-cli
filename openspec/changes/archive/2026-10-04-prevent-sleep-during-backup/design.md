## Context

See `proposal.md` for motivation. `BackupCommand` owns all real-backup output modes (JSON, interactive progress, and plain text), while `--dry-run` follows a separate planning path. `BackupPipeline.Run` is synchronous and already owns the per-run lock and checkpointed manifest batch. Sleep management is a CLI process concern and does not need to become part of the application pipeline or SQLite transaction.

## Goals / Non-Goals

**Goals:**

- Make one scoped Windows power request cover the entire real backup operation, independent of output mode.
- Ensure native resources are released on normal return and exception/cancellation paths.
- Keep inability to establish or clear the request visible without changing the backup result.
- Keep native interop compatible with the CLI's Native AOT publish.

**Non-Goals:**

- Prevent user-initiated sleep, override device or Modern Standby power policy, or guarantee that Windows remains awake indefinitely.
- Keep the display on or modify the user's power plan.
- Change backup pipeline, transaction, checkpoint, or cancellation semantics.

## Decisions

### Use the handle-based Windows Power Request API

Use `PowerCreateRequest`, `PowerSetRequest(PowerRequestSystemRequired)`, `PowerClearRequest`, and `CloseHandle`. The request is scoped by a handle, which makes its lifetime explicit and does not depend on which thread runs the backup. Use source-generated `LibraryImport` interop following the existing AOT-safe Win32 interop convention. Guard calls by platform so unsupported environments do not attempt to load Windows APIs.

`PowerRequestSystemRequired` prevents automatic system idle sleep without requesting that the display stay on. Do not use `PowerRequestDisplayRequired`.

The older `SetThreadExecutionState` API was considered but not selected: its thread-associated state is a less natural fit for an operation whose work may run on multiple threads. A third-party package is unnecessary for this small native API surface.

### Scope one request around every real-backup output mode

After resolving the profile and creating profile services, acquire the request immediately before entering the real-run path. Keep JSON, live display, and plain-output pipeline execution inside the same scope so they cannot drift in behavior. Do not acquire a request for `--dry-run`, which uses `PlanOnly` and performs no backup writes.

Use a small injectable boundary or factory at the command composition seam so CLI tests can verify acquire/release behavior without invoking native APIs. The production implementation owns the native handle and clears the request before closing it. It must clean up a created handle even if activation fails.

### Degrade visibly when Windows power management fails

Treat request creation or activation failure as a warning on standard error, then continue without an active request. Report cleanup failures to standard error as well, but do not allow a cleanup error to hide a pipeline exception or overwrite the completed backup outcome. Keep `--json` stdout limited to its existing JSON payload.

## Risks / Trade-offs

- [Windows, firmware, battery, or Modern Standby policy may still permit sleep despite the request] -> Describe this as a request against automatic idle sleep, not an absolute guarantee; retain normal backup interruption recovery.
- [Native handle cleanup can fail] -> Report cleanup failure to stderr and avoid masking the run result.
- [A power request may remain active if the process is forcibly terminated] -> Scope the request to a handle whose lifetime ends with the process; Windows releases the handle-backed request when the process exits.
