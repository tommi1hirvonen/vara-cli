## Context

See proposal.md for motivation. Relevant existing shape (all in `src/Vara.*`):

- `RestoreCommand.RunRecursiveRestore` resolves the directory, computes `asOf` from `--at` (or
  `null` for "current"), calls `SnapshotHistoryService.PlanDirectoryRestore(path, asOf, ...)`
  (read-only), shows one confirmation, then executes the plan.
- `PlanDirectoryRestore`/`ListDirectory` both derive a point-in-time subtree view by calling
  `ISnapshotRepository.GetCurrentState()`/`GetStateAsOf(asOf)` (whole-profile dictionaries of
  `CurrentFileState` keyed by path) and filtering client-side with `TryGetDescendantSubPath`/
  `NormalizeDirectoryPrefix` - there is no prefix-scoped SQL query anywhere today.
- `Snapshot` (id, `StartedAt`, `CompletedAt`, `Status`, `Stats`) is the only run-level record;
  every `FileVersionRecord` written during one run shares the exact same `RecordedAt` (set once,
  in `BackupPipeline`, as `startedAt`, and threaded unchanged through `BackupExecutor.Execute`).
  A move writes two rows in the same run: `Deleted` at the old path, `Moved` at the new path
  (`BackupExecutor.cs` `ExecuteMetadataOnlyOperation`).
- The single-file interactive picker (`RestoreCommand`, "Interactive version selection when
  restoring") uses `StandardError.Console.Prompt(new SelectionPrompt<FileVersionRecord>()...)`
  with `UseConverter` producing a plain string per choice - Spectre renders that string as
  markup, so any per-row coloring has to be embedded as markup text, not as a `Style` object
  (unlike `Table.AddRow`, which colors via `Style`-carrying `Text` cells).
- `SnapshotsTablePresenter.StatusStyle` already maps `Complete -> OutcomeStyle.Success`,
  `Cancelled -> OutcomeStyle.PartialFailure`, `Failed -> OutcomeStyle.Error`.

## Goals / Non-Goals

**Goals:**
- Let an interactive `restore --recursive` choose among the snapshots that actually changed
  something under the requested directory, plus "current", instead of only "current".
- Give each candidate entry directory-scoped change stats and directory-scoped point-in-time
  totals (files, bytes, symlinks/junctions counted separately), computed reliably.
- Add a non-interactive `--snapshot <id>` equivalent.
- Preserve today's non-interactive default (`--recursive` with no `--at`/`--snapshot` silently
  restores current state) exactly as-is.

**Non-Goals:**
- No change to how a directory restore is actually executed (`PlanDirectoryRestore`/
  `ExecuteDirectoryRestore` stay as they are) - the picker only decides which `asOf` value feeds
  the existing planning call.
- No change to the single-file picker's behavior, options, or requirement.
- No general-purpose "diff directory between two snapshots" feature - stats are always relative
  to "current" vs. the shown point in time, per-entry, not pairwise between arbitrary entries.

## Decisions

### Enumerating candidates and their deltas: one new prefix-scoped repository query
Add `IReadOnlyList<FileVersionRecord> GetFileHistoryUnderPrefix(string prefix)` to
`ISnapshotRepository`/`SqliteSnapshotRepository`: every `file_versions` row whose `relative_path`
falls under the directory prefix (same descendant test as `TryGetDescendantSubPath`), across all
snapshots, ordered most-recent-first (matching `GetFileHistory`'s convention). Because a move
writes rows at both the old and new path, filtering purely on `relative_path` already captures a
file moving into or out of the directory correctly - no separate handling of
`previous_relative_path` is needed (see Context: two rows per move, in the same snapshot).

This single query serves both jobs:
- **Candidate snapshots**: `GROUP BY snapshot_id` over the rows, joined against `ListSnapshots()`
  for `StartedAt`/`Status`; a run with zero rows here never appears (it never touched the
  subtree), and `Running`/`Failed` runs are filtered out by status.
- **Per-snapshot delta stats**: `GROUP BY snapshot_id, change_kind` (+ `SUM(size)`) over the same
  rows gives added/changed/moved/deleted counts and net bytes for that run, scoped to the
  directory - unambiguous, since each row is a discrete recorded event, not a merged state.

Alternative considered: compute candidates by scanning `ListSnapshots()` and calling
`GetStateAsOf`/`GetFileHistory` per snapshot to test membership. Rejected - `GetStateAsOf` only
returns *current-as-of* state, not "did this specific run touch this path", so it can't answer
"which snapshots touched this subtree" without an O(snapshots x profile size) rescan; the new
query answers it in one pass.

### Point-in-time totals: reuse `GetStateAsOf`, not a hand-rolled replay
For each candidate snapshot, "how many files (and symlinks, and bytes) does the directory contain
as of this snapshot" is computed by calling the already-existing
`ISnapshotRepository.GetStateAsOf(snapshot.StartedAt)` and filtering the returned dictionary by
`TryGetDescendantSubPath`, splitting on `CurrentFileState.IsLinked` for the separate
symlink/junction figure - exactly the same computation `PlanDirectoryRestore` already performs for
whichever single point in time a restore actually targets.

Alternative considered: an incremental single-pass replay over `GetFileHistoryUnderPrefix`'s rows
(sorted chronologically, maintaining a running live-path set as each snapshot's rows are applied)
would be one pass instead of N whole-profile recomputations. Rejected in favor of reusing
`GetStateAsOf`: the candidate list is bounded by "snapshots that touched this one subtree" (small
relative to a profile's total snapshot count in the typical case of restoring a narrow
subdirectory), and reusing the exact function the restore itself calls guarantees the displayed
total can never drift from what selecting that entry would actually produce. A hand-rolled replay
would need to reproduce `GetStateAsOf`'s own same-run tie-break rule (`MAX(id)` per path, see
Risks) to stay consistent, which is unnecessary duplication for a bounded-N list. If profiles with
very large candidate counts and very large total tracked-path counts turn out to make this slow in
practice, the replay approach remains available as a follow-up optimization without changing the
requirement or the command surface.

### `--snapshot <id>` option and mutual exclusivity
Add `--snapshot <long>` to `RestoreCommand`, alongside the existing `--at`/`--version`/
`--recursive` options:
- Rejected (existing error style) when combined with `--at` (mirrors the existing `--at`/
  `--version` check).
- Rejected when given without `--recursive` (mirrors the existing `--recursive`/`--version`
  check).
- When given with `--recursive`, resolves directly to `asOf = <that snapshot's StartedAt>` and
  skips the picker entirely (same code path the picker's own selection feeds into) - validated
  against the same candidate set the picker would show (must be `Complete`/`Cancelled` and have
  at least one row under the directory), reusing `GetFileHistoryUnderPrefix` for that check so the
  interactive and non-interactive paths can never disagree about what counts as a valid target.

### Picker mechanics: reuse `SelectionPrompt`, add default selection and markup coloring
The directory picker is a second `StandardError.Console.Prompt(new SelectionPrompt<...>())`,
parallel to the single-file one, choosing between a small "current state" sentinel entry and the
candidate snapshots (most recent first, current always on top since it represents "now"). Spectre
supports pre-selecting a default via the prompt's default-value/highlight support; the "current"
sentinel is placed first and marked default so accepting without navigating reproduces the plain
`--recursive <dir>` behavior. Row text embeds Spectre markup directly (e.g.
`[palegreen1]Complete[/]` / `[lightgoldenrod2]Cancelled[/]`) reusing the exact same colors
`SnapshotsTablePresenter.StatusStyle` already uses for these two statuses, so a row's color always
matches what `vara snapshots` would show for the same run.

### Interactive default change is scoped to interactive sessions only
`RunRecursiveRestore`'s existing non-interactive fallback (`--recursive` with no `--at`: silently
restore current state) is untouched. The picker only replaces that silent default when
`canPromptForVersion` (already computed today, just currently gated to `!recursive`) is true -
the same live-input/live-console gate the single-file picker already uses.

## Risks / Trade-offs

- **[Risk]** A single run that both moves a file out of path A and moves a different file into
  path A ends up with two rows sharing `relative_path = A` in that run; `GetStateAsOf`'s
  `MAX(id)`-per-path tie-break decides which one "wins" for point-in-time totals, based on
  insertion order rather than any semantic ordering. **Mitigation**: this is an existing
  characteristic of `GetCurrentState`/`GetStateAsOf`, already relied on by today's directory
  restore/listing - the picker inherits it rather than introducing a new inconsistency, and it
  only affects the same, already-narrow edge case.
- **[Risk]** Per-candidate `GetStateAsOf` calls are O(profile's total tracked paths) each; a
  directory with many qualifying snapshots on a very large profile means that cost N times.
  **Mitigation**: bounded by "snapshots that touched this one subtree", typically small; flagged
  above as a follow-up (incremental replay) if it proves too slow in practice, without needing a
  spec or command-surface change.
- **[Trade-off]** Showing both directory-scoped delta stats and point-in-time totals per row is
  more information than the single-file picker's one-line-per-version format; row text will be
  visibly longer. Accepted per explicit request; no truncation/column-budget handling beyond what
  `SelectionPrompt` already wraps/pages on its own (`PageSize` is already used by the single-file
  picker for the same reason).

## Migration Plan

Additive only: a new command option (`--snapshot`), a new repository method, and a new
interactive prompt that only activates in the specific case (`--recursive`, interactive session,
neither `--at` nor `--snapshot` given) that today silently restores current state with no
confirmation step of its own. No data migration. No removal of existing options or behavior for
non-interactive sessions. Rollback is a revert of the command/service changes; no persisted state
format changes.
