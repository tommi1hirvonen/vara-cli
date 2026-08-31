## 1. Fix the case-sensitive lookup

- [x] 1.1 In `SqliteSnapshotRepository.GetCurrentState()`, construct the result dictionary as
  `new Dictionary<string, CurrentFileState>(StringComparer.OrdinalIgnoreCase)`, matching the
  comparer already used by `GetFileHistory`'s `visited` set in the same class. Verify the
  project builds.

## 2. Regression tests

- [x] 2.1 In `tests/Vara.Infrastructure.Tests/Snapshots/SqliteSnapshotRepositoryTests.cs`, add a
  test asserting that `GetCurrentState()` returns an entry when looked up by a path differing
  only in casing from the recorded `relative_path` (e.g. record `a.txt`, look up `A.TXT`), and
  that the returned dictionary exposes exactly one entry for that path rather than one per
  casing variant. Verify the new test passes.
- [x] 2.2 In `tests/Vara.Application.Tests/Backup/BackupDifferTests.cs`, add a test alongside
  `A_path_with_matching_size_and_mtime_is_unchanged` asserting that a scanned entry whose path
  differs from the current-state key only in casing (with matching size/modified time) is
  classified as unchanged, not as an addition, using a `currentState` dictionary constructed
  with `StringComparer.OrdinalIgnoreCase` (mirroring the production code's comparer). Verify
  the new test passes.
- [x] 2.3 In `tests/Vara.Application.Tests/Backup/Fakes.cs`, construct
  `FakeSnapshotRepository.GetCurrentState()`'s result dictionary with
  `StringComparer.OrdinalIgnoreCase`, matching the production fix, so the fake stays faithful to
  the real `ISnapshotRepository` implementation it stands in for. Then, in
  `tests/Vara.Application.Tests/Backup/BackupPipelineTests.cs`, add an integration-level
  regression test (modeled on `Running_twice_against_an_unchanged_source_performs_zero_content_transfer_the_second_time`)
  that runs a backup, renames a source file changing only its casing (content and modification
  time unchanged), runs a second backup, and asserts: no new file content is copied/hashed for
  that path, `GetCurrentState()` still reports exactly one entry for the path, and no manifest
  row for the file is left in a non-deleted state under both the old and new casing. Verify the
  new test passes.
  - Note: this test still runs against `FakeSnapshotRepository`, not the real
    `SqliteSnapshotRepository`, because `Vara.Application.Tests` has no project reference to
    `Vara.Infrastructure` and no cross-layer integration test project exists in this repo today.
    Flag this as a follow-up after this change is archived - see design.md's Open Questions.

## 3. Full verification

- [x] 3.1 Run the full test suite (`dotnet test`) and verify all tests pass, confirming the fix
  does not regress existing case-sensitive-path assumptions elsewhere in the codebase.
