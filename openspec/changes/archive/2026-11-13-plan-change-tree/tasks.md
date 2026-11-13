## 1. Shared groundwork

- [x] 1.1 Move `ChangeEntry` to a file compiled before `Backup/Run.fs`, keeping its struct shape, and verify the whole solution builds and the existing tests pass unchanged
- [x] 1.2 Split `HistoryView.ofRollup` into `treeLines` (scope, source roots, depth, rollup) plus the snapshot header, and verify the existing static snapshot output tests pass unchanged
- [x] 1.3 Move depth parsing into a shared helper and add the shared "present this tree" step (interactive when `canPrompt`, otherwise static plus the one-line note on standard error when `--interactive` was given); switch `SnapshotCommand` to both and verify the existing snapshot command tests, including the fallback and invalid-depth tests, pass unchanged

## 2. Plan to entries

- [x] 2.1 Add `Run.entriesOfPlan` mapping transfers, moves, deletes and links to `ChangeEntry` values, and verify Domain tests for each kind, including a move's `MovedFrom` and a link counted as linked
- [x] 2.2 Add the detail flag to `BackupRun.dryRun`, returning the summary and, only when asked, the mapped entries, with the existing function kept as a wrapper with detail off; verify Engine tests that the summary is identical with and without detail, that no entries are returned without it, and that the manifest, mirror, content store and snapshot list are unchanged after a dry run with detail
- [x] 2.3 Verify with an Engine test that the entries' added, changed, moved and deleted counts equal the summary's counts for the same plan

## 3. Backup command

- [x] 3.1 Add `--depth`, `--files` and `--interactive` to `vara backup`, reject a negative or non-integer depth and any combination with `--json` before profile resolution, and verify CLI tests that nothing is scanned or run in those cases
- [x] 3.2 Show the dry-run tree after the summary from the plan's entries (static, and interactive through the shared step), with no tree when nothing is planned, and verify CLI tests for the depth, files, no-change, root-count-equals-summary and scripted interactive scenarios
- [x] 3.3 Show the real-run tree after the summary from the recorded snapshot read read-only, including partial and cancelled runs, no tree for a refused run, and the "no changes recorded" message for an empty snapshot; verify CLI tests for each, and that `backup --depth n` and `snapshot <id> --depth n` show the same tree
- [x] 3.4 Withhold the interactive view after a cancelled run (static tree, no note), and verify a CLI test with the cancellation token set
- [x] 3.5 Return the run's own outcome whatever the browser does, and verify CLI tests for quit, Ctrl+C in the tree, the fallback, and a partial-failure run, each with the expected exit code
- [x] 3.6 Handle a failed manifest read after a real run by reporting the error after the summary without changing the run's outcome, and verify with a test that injects the failure

## 4. Benchmarks and release check

- [x] 4.1 Measure the planning stage in `BackupStageBenchmarks` on a large synthetic source with detail off and on, recording allocations and peak working set; verify detail off matches the current baseline and record the cost of detail on in the benchmark results
- [x] 4.2 Re-run the snapshot rollup allocation measurement used for `interactive-change-tree` after moving `ChangeEntry`, and verify the numbers are identical to those recorded there
- [x] 4.3 Run the scanner and transfer benchmarks only if `BackupRun`'s execution path or the scan observers were touched, and verify no regression against the baseline; if `BackupRun` changed, re-record the release check baseline

## 5. Documentation and checks

- [x] 5.1 Update the README and the `backup` help text for the three options, the dry-run and real-run behavior, and the `--json` exclusion, and verify `vara backup --help` shows them
- [x] 5.2 Run the full test suite, then manually run `vara backup <profile> --dry-run --interactive` and a real `--interactive` run on Windows Terminal and conhost, covering navigation, scrolling, Ctrl+C in the tree, a cancelled run and redirected output, and record the result
