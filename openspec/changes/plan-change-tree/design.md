## Context

`interactive-change-tree` gives `vara snapshot` a static and an interactive change tree. It introduces `ChangeEntry`, `History.rollupEntries`, `TreeBrowser`, the `KeyInput` record and `TreeSession.run`, and it assumes a header `Display` supplied by the caller (`TreeBrowser.create` takes one; the static `HistoryView.ofRollup` builds a snapshot header itself). See proposal.md for scope.

Today `BackupRun.dryRun` calls `planFor`, which produces a `BackupPlan` (`Metadata`: moves, deletes, links; `Transfers`: adds and changes, each carrying a `MirrorPath`), then immediately reduces it with `Run.summarizePlan` to `PlanSummary`. The plan is discarded inside the engine. `BackupRun.run` returns a `RunResult` that carries the new `SnapshotId`, and the snapshot's rows are readable afterwards through `ManifestQueries.snapshotVersions`, as `vara snapshot` does. `BackupCommand` shows a live scan display during planning and the transfer display during a run, then prints the summary.

`ChangeEntry` is defined in `Snapshots/History.fs`, which compiles after `Backup/Run.fs`, so the plan-to-entries mapping cannot sit next to `summarizePlan` without moving the type.

## Goals / Non-Goals

**Goals:**
- One tree implementation for snapshots, dry runs and real runs, differing only in where the entries come from and which header is shown.
- No extra cost for a plain dry run or backup.
- A command outcome that the tree cannot change.

**Non-Goals:**
- Changing planning, move detection or the executor.
- A confirm prompt between a dry-run tree and a real run.
- Streaming the tree while the run is in progress.

## Decisions

**1. Dry run builds its tree from the plan; a real run builds it from the manifest.** After a real run, a snapshot exists, so the tree is read from it exactly as `vara snapshot` does. This avoids keeping the plan alive through execution, shows what was actually recorded (including partial and cancelled runs), and means `backup --depth 2` and `snapshot <id> --depth 2` agree by construction. Alternative considered: retain the plan for both modes. Rejected because it holds every planned operation through the whole transfer, and a failed or skipped file would appear in the tree as if recorded.

**2. The plan's detail is requested, not always produced.** `BackupRun.dryRun` gains a flag for detail. With it off, it returns the summary and an empty list and behaves as today, so a plain `--dry-run` does not retain anything. With it on, it returns the summary and the plan's changes as `ChangeEntry` values, mapped inside the engine call before the lock and manifest are released, so no `BackupPlan` or `Transfer` escapes the engine. Alternative considered: return the `BackupPlan` itself. Rejected because it leaks engine types, with their source paths and hashes, into presentation, and keeps more alive than the tree needs.

**3. `ChangeEntry` moves earlier.** It moves out of `Snapshots/History.fs` into a small file compiled before `Backup/Run.fs` (next to `MirrorPath`), keeping its shape and its `[<Struct>]` attribute. The mapping `Run.entriesOfPlan` sits next to `summarizePlan`: an added transfer is `Added`, a changed transfer `Changed`, a move `Moved` with `MovedFrom`, a delete `Deleted`, a link `Linked`. `History.fs` keeps `rollupEntries` and opens the type from its new place. This is a pure move; the benchmark in tasks verifies no allocation change in the snapshot path.

**4. The plan's totals and the tree's totals share one source.** The summary counts and the entries come from the same plan in the same call, so the root row's counts equal the dry-run counts, except links, which `PlanSummary` does not count and the tree reports under its linked column. The test for the "tree totals match" scenario asserts this.

**5. A header-agnostic tree view.** `HistoryView.ofRollup` is split: a `treeLines` function takes the scope, source roots, depth and rollup and returns the tree lines, and `ofRollup` becomes the snapshot header plus `treeLines`. The dry run and real-run static output reuse `treeLines` under their own summary. `TreeBrowser.create` already takes a header, so the interactive tree needs no change.

**6. One shared "present this tree" step.** A function in the CLI project takes the header (or none), scope, source roots, depth, rollup and the interactive flag, and either runs `TreeSession.run` when `HistorySession.canPrompt` holds, or prints the static tree and, when `--interactive` was given, the one-line note on standard error. `SnapshotCommand` and `BackupCommand` both call it. The depth parsing moves to a shared helper next to it, so both commands reject the same invalid values with the same message.

**7. The outcome belongs to the run.** The command's return value is the dry run's `planOutcome` or the real run's `Run.outcome`; the result of `TreeSession.run` is discarded. This keeps a successful run at exit code 0 when the user presses Ctrl+C in the browser, and keeps a partial failure at 2 after quitting. The `snapshot` command keeps using the session's result as in `interactive-change-tree`.

**8. Order of events.** Planning or the run finishes and its display ends, the summary is printed, and only then is the tree shown. For a dry run the lock is already released by the time `dryRun` returns. For a real run the tree reads the manifest read-only after the run's manifest handle is closed. A cancelled real run shows the tree statically: the user has just asked to stop, so opening a full-screen view would be surprising, and the cancellation token is already set. The static tree still honors `--depth` and `--files`, and no fallback note is written because the interactive view was withheld deliberately, not unavailable.

**9. Real-run target resolution.** The tree needs the profile's target layout and source roots, which `HistorySession.resolve` already derives from a config path and profile name. `BackupCommand` resolves them again after the run, from the same inputs, and reads the manifest through `HistorySession.withManifest`. Alternative considered: thread the layout out of the engine. Rejected as a larger interface change for a lookup the history commands already do.

**10. `--json` exclusion is checked first.** The usage error is raised before the profile is resolved, using the same mutual-exclusion helper the history commands use, so no scan or run starts.

**11. Tree options select the view.** `--depth`, `--files` and `--interactive` each turn on the tree. There is no separate `--tree` flag: `--interactive` implies it and `--depth` or `--files` alone print the static tree, as agreed during exploration.

## Risks / Trade-offs

- [Retaining per-path changes for a large first-time dry run raises memory] → Only done when a tree option is given; entries are structs and the plan itself is not kept; measure the planning stage with and without the option in `BackupStageBenchmarks`, both allocations and peak working set, on a large synthetic source.
- [Moving `ChangeEntry` could change the snapshot path's allocations] → It is a file move with the same struct shape; re-run the rollup measurement used for `interactive-change-tree` and require identical numbers.
- [Reading the manifest after a real run could fail or find the snapshot missing] → Treat a failed read as a hard error reported after the summary, without changing the run's outcome, and show no tree.
- [A tree after a very long run is surprising if the terminal was left unattended] → The interactive view starts only when input is a terminal; unattended runs have none and fall back to the static tree.
- [The dry-run tree omits `Unscanned` paths] → They are already listed in the summary above the tree; the tree shows only planned changes.
- [Changing `BackupRun.dryRun`'s signature touches engine tests] → Keep the existing function as a wrapper with detail off, so existing callers and tests are unchanged.

## Open Questions

- Whether a future `--json` should carry the changes as an array is deferred; it does not affect these specs or tasks.
