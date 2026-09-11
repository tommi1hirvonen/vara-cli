## 1. Repository layer: prefix-scoped history query

- [ ] 1.1 Add `GetFileHistoryUnderPrefix(string prefix)` to `ISnapshotRepository`
  (`Vara.Core/Abstractions/ISnapshotRepository.cs`), documented as returning every
  `FileVersionRecord` whose `RelativePath` is a descendant of `prefix`, across all snapshots,
  most-recent-first.
- [ ] 1.2 Implement it in `SqliteSnapshotRepository`
  (`Vara.Infrastructure/Snapshots/SqliteSnapshotRepository.cs`), matching descendants the same
  way `SnapshotHistoryService.TryGetDescendantSubPath`/`NormalizeDirectoryPrefix` already define
  "under this directory" (reuse or mirror that normalization so the two never disagree), ordered
  by `id DESC`, and verify with new unit tests in
  `tests/Vara.Infrastructure.Tests/Snapshots/SqliteSnapshotRepositoryTests.cs` covering: a file
  directly under the prefix, a file several levels deeper, a file outside the prefix (excluded),
  and both rows of a move that crosses the prefix boundary in each direction.
- [ ] 1.3 Add/extend an integration test in
  `tests/Vara.IntegrationTests/History/SnapshotHistoryServiceRealPortsTests.cs` exercising the new
  query against a real repository across multiple snapshots.

## 2. Application layer: candidate snapshots and directory-scoped statistics

- [ ] 2.1 Add a `DirectorySnapshotCandidate` (or similar) record to
  `Vara.Application/History/SnapshotHistoryService.cs` capturing, per candidate snapshot: the
  `Snapshot` itself, directory-scoped added/changed/moved/deleted counts and net byte delta for
  that run, and the directory's total file count, total byte size, and total symlink/junction
  count as of that snapshot's `StartedAt`.
- [ ] 2.2 Add `SnapshotHistoryService.ListDirectorySnapshotCandidates(string directoryPath)`:
  groups `GetFileHistoryUnderPrefix` rows by `SnapshotId` to find candidates and their per-run
  directory-scoped deltas, filters to `Snapshot.Status` in `{Complete, Cancelled}` (drop
  `Running`/`Failed`), and for each candidate calls `GetStateAsOf(candidate.StartedAt)` filtered
  by the directory prefix to compute the point-in-time file/byte/symlink totals (splitting on
  `CurrentFileState.IsLinked`), ordered most-recent-first. Verify with unit tests in
  `tests/Vara.Application.Tests/History/SnapshotHistoryServiceTests.cs` covering: a directory with
  no history (empty candidate list, no throw), multiple qualifying snapshots, a `Running`/`Failed`
  snapshot present but excluded, a snapshot with only an unrelated path elsewhere in the profile
  (excluded), and correct symlink/junction counts kept separate from the file count.
- [ ] 2.3 Add `SnapshotHistoryService.ResolveDirectorySnapshot(string directoryPath, long
  snapshotId)` used by the new `--snapshot` option: validates the id is a member of
  `ListDirectorySnapshotCandidates` for that directory and returns its `StartedAt` (as the `asOf`
  to feed into the existing `PlanDirectoryRestore`), throwing a clear, dedicated exception
  otherwise (nonexistent id, wrong status, or no rows under the directory). Verify with unit
  tests covering a valid id, an id from an unrelated directory, and an id in `Running`/`Failed`
  status.

## 3. CLI layer: `--snapshot` option and the interactive picker

- [ ] 3.1 Add the `--snapshot <long>` option to `RestoreCommand.Create`
  (`Vara.Cli/Commands/RestoreCommand.cs`), rejecting it (existing `OutcomeStyle.WriteLineError` +
  exit 1 pattern) when combined with `--at`, and when given without `--recursive`.
- [ ] 3.2 In `RunRecursiveRestore`, when `--snapshot` is given, resolve it via
  `ResolveDirectorySnapshot` and use its `StartedAt` as `asOf`, bypassing the picker.
- [ ] 3.3 When neither `--at` nor `--snapshot` is given and the session is interactive (the
  existing `canPromptForVersion` gate, no longer restricted to `!recursive`), call
  `ListDirectorySnapshotCandidates` and present a `SelectionPrompt` with a "current tracked
  state" entry first (pre-selected as default) followed by the candidates most-recent-first;
  selecting "current" keeps `asOf = null`, selecting a candidate sets `asOf =
  candidate.StartedAt`. When the session is not interactive, keep today's behavior (silently
  restore current state) unchanged.
- [ ] 3.4 Render each candidate row with directory-scoped delta stats and point-in-time totals
  (files, bytes, symlink/junction count reported separately), embedding Spectre markup for
  `Complete`/`Cancelled` coloring that matches `SnapshotsTablePresenter.StatusStyle`'s existing
  color choices, escaping any interpolated text that isn't already known-safe.
- [ ] 3.5 Update the `--at`, `--version`, and new `--snapshot` option descriptions in
  `RestoreCommand.Create` to reflect the new behavior and mutual-exclusion rules.
- [ ] 3.6 Extend `tests/Vara.Cli.Tests/Commands/RestoreCommandTests.cs` covering: `--snapshot`
  combined with `--at` is rejected, `--snapshot` without `--recursive` is rejected, `--snapshot`
  with a valid id resolves and restores without prompting, and (for the interactive path, using
  whatever console/input double the existing single-file-picker tests already use) the picker is
  shown only when interactive and omits `Running`/`Failed` snapshots.

## 4. Spec/documentation follow-through

- [ ] 4.1 Confirm `openspec validate --strict` passes for this change once specs/design/tasks are
  all written.
- [ ] 4.2 Update `README.md`/CLI help text (if the project documents `restore` usage there
  outside of the command's own `Description`/`Option` strings) to mention `--snapshot` and the
  directory picker.
