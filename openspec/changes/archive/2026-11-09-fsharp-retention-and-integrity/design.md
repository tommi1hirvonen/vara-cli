## Context

See proposal.md for why. This design builds on `fsharp-snapshot-history`, which is applied before it, and reuses what that change established instead of defining it again.

What is in place, and what this design uses:

- **Domain** (`Vara.Domain`): `ManifestRow`, `FileVersion`, `SnapshotInfo` (id, start and completion times, `SnapshotStatus`, `Counts`), `Moment`, `HistoryError`, `Profile` with its optional `RetentionPolicy` (four `RetentionCount`s), `Outcome`, `ContentHash`, `Counts`. Presentation: `Display` (lines, `Span`, `Table`, `Box`, `Tone`), `Display.Wording`, `Tracker` and `ProgressEvent`, `ByteSize`, `Elapsed`, `PathLabel`, `OutputMode`, `Capabilities` (with `InputIsInteractive`).
- **Engine** (`Vara.Engine`): `SqliteManifest` (`openAt` for a writer, `openReadOnly` for a reader, lazy row queries, `snapshots`), `ContentStore` (`layout`, `blobPath`, `openRead`, `StoreLayout`), `RunLock`, `Flow` (`attempt`, `rethrow`), `RestoreRun` (the plan-then-execute shape and `applyAll`, which stops between items), `Kernel32`, `ProfileResolution`.
- **Cli** (`Vara.Cli`): `CliEnv` (consoles, `Capabilities`, `Prompts`, `CurrentDirectory`, `UtcOffsetAt`, run environment, power calls), `Prompts` (`Select`, `Confirm`), `HistorySession` (`resolve`, `withManifest`, `show`, `fail`, `exactlyOne`, `mutuallyExclusive`, `firstRefusal`), `RunProgress.withProgress` (takes a publisher of `ProgressEvent`s), `ProgressDisplay.showWith`, `CommandAction`, `GracefulCancellation`, `LiveRenderer`, `PlainRenderer`.
- **Tests**: `Vara.Testing.SpecCoverage`, the in-process command harness (`invokeEnv`, `scratch`, `backupOnce`, `runEnv`, scripted `Prompts`), and the process-based parity pattern (`start`, `csharpDll`, `fsharpDll`).

The C# code is the behavioral reference, not a design: `RetentionEvaluator`, `PruneService`, `PruneCommand`, `IntegrityCheckService`, `IntegrityCheckProgressCalculator`, `CheckCommand`, `PruneOutcomeReporter` and `CheckOutcomeReporter`.

## Goals / Non-Goals

**Goals:**

- Every owned scenario is verified by a test named after it. Retention evaluation and the check verdict are tested on plain data before any file or database is involved.
- A prune never removes anything the user was not shown, never touches the live mirror, and can be previewed as the plan itself.
- Both commands show count-based or byte-weighted progress through the same bracket as backup and restore, with no second progress model.
- Parity with C# where it can be compared mechanically: the manifest rows and blobs that remain after a prune, findings and exit codes of a check.

**Non-Goals:**

- A `--json` mode (the spec scopes it to `backup`).
- Profile editing (a later change).
- Any change to the retention or integrity specs. A difference found while porting is recorded as a C# bug fix with a parity test first.
- Parallel verification beyond what the C# check does today. The spec says "bounded-parallel" only in the proposal's summary; the C# loop is sequential and the spec fixes no concurrency, so the first version is sequential and the verdict function takes one entry at a time. Parallel reads can be added behind the same function without changing the spec.

## Decisions

### D1. Retention evaluation is a pure function over snapshot data

`Retention.evaluate : RetentionPolicy -> SnapshotInfo list -> RetentionDecision`, where a decision holds the kept ids and the eligible ids. Each tier is a bucketing function from a UTC date to a bucket key (`day`, `Monday of the week`, `first of the month`, `first of the year`), and a tier keeps the newest completed snapshot of each of the newest `count` buckets. The policy is the union of the four tiers plus the newest completed snapshot. A snapshot's moment is its completion time when it has one, otherwise its start time, always read as UTC (`UtcDateTime`), so every tier buckets a snapshot the same way whatever offset was recorded. A `Running` snapshot is never eligible; `Failed` and `Cancelled` snapshots are eligible whenever they are not kept, because only `Complete` snapshots take part in the tiers. The result is a set, so input order cannot change it.

Alternatives: one pass that assigns a snapshot to its tiers (rejected: four small functions compose and test separately); reusing a date library (rejected: no dependency is needed for four keys).

### D2. A prune is a plan, then an execution, and the plan is what the user confirms

`Prune.plan` is pure. It takes the evaluated eligible ids (from D1), the snapshot ids the user confirmed (when there was a prompt), the ids that still exist, and later, once the rows are gone, the hashes still referenced and the hashes stored. It returns:

- `Prune.snapshotsToRemove : eligible -> confirmed voption -> existing -> int64 list`: the eligible snapshots, narrowed to the confirmed ones, narrowed to those still present. A snapshot that became eligible after the prompt is not in the confirmed list, so it stays; a confirmed snapshot that is gone is dropped without an error.
- `Prune.blobsToRemove : stored -> referenced -> ContentHash list`: set difference, in a stable order.

The shell runs the steps under the run lock: evaluate and remove the snapshot rows in one transaction (an atomic step, as the cancellation requirement says), then read the referenced hashes, list the stored blobs, plan the removals, and delete them one by one, checking `ShouldStop` between blobs. A preview is the plan rendered as a `Display`; the prompt reads `snapshotsToRemove` for its count. Pruning rows keeps C#'s rule that a row which is the current (latest, not deleted) row of its path is never removed, so the current state survives even when its snapshot expires; that rule is a SQL `NOT IN (latest rows)` in the write, and is covered by a test on a real manifest ("Live mirror unaffected by pruning").

The three failure levels hold. No policy, a busy target, a missing confirmation and an unreadable lock are `Result` errors before anything starts. A blob that cannot be deleted because of an I/O error is a value (`BlobFailure`) that the run records and continues past; the outcome is then `PartialFailure`. Anything else is an exception, rethrown with its stack.

Alternatives: delete rows and blobs in one pass (rejected: the spec makes row removal one atomic step and garbage collection a second, cancellable one); keep a mutable "to remove" list in the service (rejected by the series rules).

### D3. The manifest gets a writer for prune and queries for both commands

`SqliteManifest` gains, in the existing module:

- `removeSnapshots : ManifestHandle -> int64 list -> int` with the C# semantics: delete rows of the listed snapshots except each path's current non-deleted row, then delete the listed snapshots that no row references, in one transaction, returning how many snapshots went. It uses a normal `openAt` handle (it writes), unlike every history command.
- `referencedHashes : ManifestHandle -> Set<string>` and `referencedSizes : ManifestHandle -> (ContentHash * int64) seq` (the largest size recorded for each hash), for the garbage collection and the check.
- `pathsForHash : ManifestHandle -> ContentHash -> string list`, for the paths a finding names.

Queries return rows or small sequences and contain no decision, as in `fsharp-snapshot-history` D2. `ContentStore` gains `storedHashes` (every blob name under the versions root) and `deleteBlob` (clears the read-only attribute, as C# does, then deletes).

### D4. The check is a verdict function over each blob, folded into a result

`Check.verdict : quick: bool -> expected: ContentHash -> size: int64 -> observed: Observation -> Verdict`, where `Observation` is `Absent | Present of recomputed: ContentHash voption` (nothing in quick mode) and `Verdict` is `Intact | Missing | Corrupt`. The fold collects findings (hash, up to five paths, total path count), the checked count, and the orphaned hashes (`stored \ referenced`, informational, never deleted). The shell reads each blob and hashes it (`Hashing.hashStream` through a counting stream that reports bytes as they are read), then calls the verdict. A symlink or junction row has no hash, so it never reaches the check: `referencedSizes` excludes rows without a content hash.

`Check.outcome : CheckResult -> Outcome`: `Success` with no missing or corrupt blob and no stop; `PartialFailure` otherwise, including a stop, so a cancelled run is not confused with a hard error and is distinct from "problems found" in the report, which prints both facts. The C# build returns the partial-failure code for a cancelled check too; the spec says a cancelled check "exits successfully". This is recorded as a spec/C# gap, follows C# for parity, and has a parity test that fixes the current behavior. If the gap is resolved the other way, it is a one-line change in `Check.outcome`.

Alternatives: a `CheckService` with a hasher and a store as constructor arguments (rejected: they are the shell's functions, passed in); returning findings through exceptions (rejected: expected outcomes are values).

### D5. Count-based and byte-weighted progress reuse `Tracker`, with one added credit

The bracket is the one from the backup (`RunProgress.withProgress`, `ProgressDisplay.showWith`). What changes is small and is added to the shared pieces, once:

- **Basis.** `Display.Wording` gains a `Basis` (`Bytes` or `Items`). `Display.viewWith` shows the bar and the first statistics row by the chosen basis: `Items` uses `Files n / total` for the percentage and the ETA and has no throughput row. `Tracker.percentBy` and `Tracker.remainingBy` take the basis. The default basis stays `Bytes`, so the backup and restore screens do not change, and their tests stay green.
- **Credited bytes.** A full check counts a missing blob's manifest size as completed work without having read any byte. `ProgressEvent` gains `WorkCredited of bytes`, which moves the percentage but not the read byte count that the throughput and the ETA are computed from. A `Tracker` field (`Credited`) holds it; `Tracker.percent` for the byte basis is `(read + credited) / total`.
- **Phases.** Prune and check start in the existing indeterminate phase (`Phase.Scanning`), with a label of their own, and publish `TransferStarting(totalBytes, totalItems)` once the total is known. A prune with nothing to remove never publishes it, so no bar for zero items is drawn. A check whose total byte size is zero reaches 100% on completion, which `Tracker.percent` already does for an empty total.
- **ETA.** The check's ETA is from observed work: bytes per second of hashing time for a full check (so a missing blob, which takes no hashing time, does not slow the estimate), and items per second for a quick check or a prune. It is `ValueNone` ("calculating...") until work has been observed, as `Tracker.remaining` already behaves.
- **Plain output.** Redirected output appends `Display.plainProgress` lines with percentage, `checked / total` and the ETA or "calculating", as the backup does.

Alternative: a second, count-specific tracker and display (rejected for the reason given in `fsharp-snapshot-history` D5: the rate limit, the never-regressing count and the heartbeat are the parts that were hard to get right).

### D6. Commands are pipelines over the shared stages

`prune` and `check` follow `fsharp-snapshot-history` D1: resolve the profile by name (positional, no `--profile`; `ProfileResolution.forProfile` with the configuration file), open the manifest (a writer for prune, read-only for check), project with pure functions, build a `Display`, render. They compose `HistorySession`'s stages (`fail`, `show`, `firstRefusal`) and do not extend a base. `prune` takes `--yes`/`-y`; with eligible snapshots and no `--yes` it asks `env.Prompts.Confirm` once, stating the number of snapshots, defaulting to no; a non-interactive session without `--yes` is a refusal naming `--yes`. Declining reports that the prune was cancelled and exits successfully. `check` takes `--quick`.

The run lock is `RunLock.tryAcquire`, as in the backup. The refusal message names the target as busy, not the profile; a lock file that cannot be opened for a permission reason has its own message, which the existing `BackupRefusal` rendering already distinguishes and which the prune reuses.

### D7. Cancellation follows the backup and the restore

`ShouldStop` is read between blobs, never inside one. For prune: a stop requested before the snapshot-removal step skips that step and the garbage collection entirely and reports a cancelled run; a stop during garbage collection finishes the blob in progress, starts no other, and leaves what was deleted deleted. The result maps to `Outcome.PartialFailure` and a cancelled message, as in C#. For check: a stop finishes the blob in progress, reports the findings so far, and the cancelled fact. The second Ctrl+C is `GracefulCancellation`, tested once for each command by name.

### D8. Parity is checked on state and text, in processes

The parity tests run the C# and F# `prune` and `check` as processes on copies of one target that the F# backup wrote (with several snapshots at different times, so that the tiers have something to decide) and compare the snapshot ids and blob names that remain, the findings of a check on a target with a deleted and a truncated blob, and exit codes. Interactive flows and cancellation are not compared as processes (a child cannot be driven through a prompt, and a graceful Ctrl+C cannot be delivered to one); they rest on in-process tests. Text is compared by the projected rows and counts, not by layout, because a redirected console is 80 columns wide and the C# build folds and shortens table cells, and because the C# build formats dates with the current culture.

## Risks / Trade-offs

- [A prune removes a snapshot the user did not see] → `Prune.snapshotsToRemove` intersects with the confirmed ids; a property test on random snapshot sets shows that the removed set is always a subset of both the eligible and the confirmed sets.
- [Row removal and garbage collection disagree after a crash between them] → The next prune recomputes the unreferenced set from the manifest and the store, so a blob that was missed is removed then; this is the same guarantee the C# build gives. Rows are removed first, so a crash can leave unreferenced blobs, never referenced ones missing.
- [A tier's bucket math differs from C# at a boundary] → Properties on the UTC calendar (a snapshot at `2026-01-01T00:30:00+03:00` is in 2025-12-31 for every tier) and a parity test over timestamps generated around day, ISO-Monday week, month and year boundaries compare the F# and C# kept sets.
- [Adding `Basis` and `Credited` to the shared tracker changes the backup and restore screens] → Both are additive with defaults that reproduce the old behavior; every existing backup, restore and progress test must pass unchanged, and a failure there stops the change.
- [Reading every blob of a large store is slow, and a sequential check is slower than a parallel one] → The spec fixes no concurrency; the verdict function is per blob, so parallel reads can be added later. A benchmark is not needed unless a backup hot path changes.
- [`SqliteManifest.removeSnapshots` is the first F# write outside a backup run] → It runs in one transaction under the run lock, and a test on a real manifest asserts that the current row of each path survives and that a foreign snapshot is untouched.

## Migration Plan

Nothing ships: the C# `vara.exe` stays the distributed build. To roll back, revert this change's commits. The edits to `Tracker`, `ProgressEvent` and `Display` alter code that earlier changes added; their tests stay green and are rerun in the final task. The Gate B and `LanguageComparisonBenchmarks` runs are required only when a backup hot path changed; this change adds no code to one, so a task records the check (`git diff` over the scan, executor and pipeline modules is empty) instead of a rerun.

## Open Questions

- Whether a future parallel check should keep the progress ETA formula in D5. It does not change the specs, the approach or the tasks, so it is answered when parallelism is added.
