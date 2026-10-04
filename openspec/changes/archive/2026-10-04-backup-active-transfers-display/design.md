## Context

The backup progress display runs as three Spectre `Progress()` synthetic tasks (scan, bar, stats), all driven by a single `BackupProgressColumn`. Live state is threaded through `BackupProgress` (application layer) → `BackupProgressState` (Spectre task state struct) → `RenderStats` (renderer). `BackupExecutor` currently calls three callbacks: `onBytesTransferred(long)` (chunk-level), `onTransferPhaseStarting()` (once, before transfer begins), and `onFileTransferred()` (once per completed file). There is no callback for when a file transfer *begins*, so the currently-in-flight set cannot be built without adding one.

## Goals / Non-Goals

**Goals:**
- Report which files are currently being transferred during the interactive live display.
- Reuse `PathLabelTruncator.Truncate` (already exists, handles the `...`-prefix logic) for the 48-character budget.
- Keep the existing callback pattern: all new callbacks are optional (`Action<string>?`) to avoid breaking existing call sites and tests.

**Non-Goals:**
- Showing active files in the plain-output (non-interactive) path — this path is already minimal and line-based; adding per-file noise would hurt its readability.
- Showing active files during the scan phase — only byte-transfer operations are "in flight" in a meaningful sense.
- Stable slot count (fixed number of blank lines when fewer than 3 are active) — row count grows and shrinks naturally; no fixed-height padding.

## Decisions

### `onFileStarted(string absolutePath)` callback on `BackupExecutor.Execute`

**Decision:** Add an optional `Action<string>?` callback that fires once at the start of each `ExecuteTransfer` call, before `File.OpenRead`, passing `operation.SourceAbsolutePath`.

**Rationale:** Mirrors the existing `onBytesTransferred`/`onFileTransferred` pattern. Firing before `File.OpenRead` is the earliest useful point — after that, the file is being actively read. A file that subsequently fails will leave the path in the in-flight set until the next progress snapshot removes it (it is gone from the set by the time the failure is recorded under `reportLock`). The flicker risk is minimal: the display gate already throttles redraws.

**Alternative considered:** Fire after `File.OpenRead` succeeds. Marginally safer (only fires for files that opened), but adds an extra try/catch layer and the practical difference is invisible at normal display refresh rates.

### `onFileFinished(string absolutePath)` callback on `BackupExecutor.Execute`

**Decision:** Add an optional `Action<string>?` callback that fires from `ExecuteTransfer`'s `finally` block, whether the transfer succeeds, fails with a handled I/O error, or exits through another exception.

**Rationale:** `onFileTransferred` is success-only and also drives the completed-file counter. It cannot be used to remove failed files from the in-flight set without incorrectly counting them as completed. A separate completion callback keeps those meanings distinct and ensures failed or aborted transfers do not remain displayed as active.

### Thread-safe in-flight set in `BackupPipeline`

**Decision:** `BackupPipeline.Run` owns a `ConcurrentDictionary<string, byte>` (used as a set) of in-flight paths. It passes `onFileStarted: path => inFlight.TryAdd(path, 0)` and `onFileFinished: path => inFlight.TryRemove(path, out _)`; the existing `onFileTransferred` remains responsible only for incrementing the successful-file counter and reporting progress.

**Rationale:** `ConcurrentDictionary` gives lock-free reads (needed for snapshotting into every `progress.Report` call) and atomic add/remove. The snapshot — `.Keys.ToArray()` — is taken synchronously inside each `progress.Report(...)` invocation; since `Report` is already called on the thread-pool under `TrackedProgress`, the snapshot cost (a single array allocation) is acceptable.

**Alternative considered:** A separate `IProgress<IReadOnlyList<string>>` side-channel. Cleaner separation, but doubles the number of progress channels and adds plumbing at every call site. Not worth it when `BackupProgress` already carries everything the display needs.

### `ActivePaths` field on `BackupProgress` and `BackupProgressState`

**Decision:** Add `IReadOnlyList<string>? ActivePaths` to `BackupProgress`. `BackupProgressState` adds a matching field; `ToProgress()` already reconstructs a `BackupProgress` from the struct, so the round-trip carries `ActivePaths` naturally. Both fields default to `null` / empty, so existing tests that construct `BackupProgress` directly continue to compile without modification.

**Rationale:** `BackupProgressState` is a struct stashed in Spectre's `ProgressTask.State`, which requires a `struct`. Adding a reference-type field to a `record struct` is legal; value equality still works (reference equality for the list, which is fine since we never compare progress states for equality in logic that matters).

### `RenderStats` renders active paths below existing rows

**Decision:** `BuildStatsRow` in `BackupProgressColumn` reads `progress.ActivePaths`, takes the first three entries, truncates each with `PathLabelTruncator.Truncate(path, 48)`, and appends them as additional rows in the stats `Grid`. A `+ N more` row is appended when `ActivePaths.Count > 3`. No rows are added when `ActivePaths` is null or empty.

**Rationale:** The stats task already renders a variable-height block (currently two rows). Adding rows is the natural extension. `Grid` handles variable row counts natively. The 60-character grid width accommodates the 48-character path budget plus a small leading space without wrapping.

**Alternative considered:** A fourth synthetic Spectre task for the active-files block. More modular but significantly more wiring: a new role, new state key, new `RenderContext` call in `BackupCommand`, and trickier teardown ordering. Not warranted for what is essentially a few extra rows in an existing `Grid`.

### 48-character path budget

**Decision:** 48 characters, matching the value discussed during design exploration.

**Rationale:** The stats grid is 60 characters wide (32 + 28). A 48-character path budget leaves a 12-character margin: enough to absorb the leading space and keep the display from feeling cramped, without wasting so much space that filenames are cut off unnecessarily. The existing `PathLabelTruncator` handles the truncation; no new logic is needed.

## Risks / Trade-offs

- **Fast concurrent transfers cause display flicker** — the active-paths list changes on every progress tick when concurrency > 1. The existing `ProgressDisplayGate` rate-limits redraws, which limits observable flicker. With the default `transfer_concurrency = 1`, at most one path is shown at a time. Accepted.

- **`onFileStarted` fires for files that subsequently fail** — the path appears briefly in the display before disappearing on the next tick after failure. Indistinguishable from a very fast transfer at normal refresh rates. Accepted; the alternative (fire after open succeeds) adds complexity for negligible UX benefit.

- **`ConcurrentDictionary.Keys.ToArray()` on every progress report** — one array allocation per `Report` call. At the default display-gate interval (~250 ms) and with typically small sets (1–4 entries), the allocation cost is negligible. Accepted.
