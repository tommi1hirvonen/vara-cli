## Why

`vara backup --dry-run` reports five numbers: added, changed, moved and deleted counts, and bytes to transfer. When the numbers are larger than expected, the user cannot see which directories they come from without running a real backup and inspecting the snapshot afterwards. The change tree built for `vara snapshot` (change `interactive-change-tree`) answers that question, and it can show the same view for a dry run's plan and for the snapshot a real run has just recorded.

## What Changes

- Add `--depth <n>`, `--files` and `--interactive` to `vara backup <profile>`. Any of them selects the change tree view; `--interactive` implies the tree, as on `vara snapshot`.
- For `--dry-run`, the tree is built from the computed plan, right after planning and under the same scope, rollup and display rules as the snapshot tree. It follows the dry-run summary. Nothing is written, and no snapshot is created.
- For a real run, the tree is built after the run ends from the snapshot the run recorded, read from the manifest as `vara snapshot` reads it. It follows the run summary. A run that was cancelled or had failures shows what was recorded.
- `--interactive` on either mode opens the keyboard-navigable tree from `interactive-change-tree`, with its fallback to the static tree plus a one-line note on standard error when input or output is redirected or color is off.
- The tree never changes the command's outcome or exit code. Quitting or interrupting the browser leaves the run's own outcome in place.
- `--json` stays out of scope: combining `--json` with `--depth`, `--files` or `--interactive` is rejected as a usage error.
- The dry run keeps the per-path plan only when a tree option is given. Without one, the plan is reduced to counts as today, so a plain dry run and a plain backup cost the same as before.
- Out of scope: the order or content of the existing run summary, the plan's move and delete semantics, a confirm-and-proceed prompt before a real run, and ASCII work beyond what `interactive-change-tree` already covers.

This change builds on `interactive-change-tree` (the tree browser, `ChangeEntry`, the key loop and the key-driven view requirements) and assumes it has been applied.

## Capabilities

### New Capabilities

None.

### Modified Capabilities

- `backup-execution`: new requirements for the tree options on `vara backup`, for the dry run's plan tree, for the real run's snapshot tree, and for the options' interaction with `--json`, exit outcomes and the fallback. The existing "Dry-run mode reports planned changes without executing them" requirement is unchanged.

## Impact

- `src/Vara.Engine/Runs/BackupRun.fs`: `dryRun` can return the plan's per-path changes alongside the summary when asked.
- `src/Vara.Domain/Backup/Run.fs` and `src/Vara.Domain/Snapshots/History.fs`: `ChangeEntry` moves to a file compiled earlier, and the plan is mapped to entries.
- `src/Vara.Domain/Presentation`: the tree lines are made reusable under a caller-supplied header, so a dry run is not forced to show a snapshot header.
- `src/Vara.Cli/Commands/BackupCommand.fs` and `SnapshotCommand.fs`: new options, shared depth parsing, and a shared "show or browse this tree" step.
- Tests: Engine tests for the plan's changes and for the no-detail path; Domain tests for the plan mapping; CLI tests for each option, mode and fallback.
- Benchmarks: the planning stage of `BackupStageBenchmarks` is measured with and without the retained plan, since this change keeps every planned operation alive until rendering. The scanner and transfer benchmarks are measured only if the executor or scan observers change. The release check baseline is re-recorded if `BackupRun` changes.
- No manifest, JSON or configuration changes.
