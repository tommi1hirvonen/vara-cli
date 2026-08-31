## 1. Scaffold the new test project

- [ ] 1.1 Create `tests/Vara.IntegrationTests/Vara.IntegrationTests.csproj`, following the same
  shape as `tests/Vara.Application.Tests/Vara.Application.Tests.csproj` (implicit usings,
  nullable enabled via `Directory.Build.props`, `IsPackable=false`, `IsTestProject=true`,
  unversioned `PackageReference`s for `Microsoft.NET.Test.Sdk`, `xunit`,
  `xunit.runner.visualstudio`), with `ProjectReference`s to `src/Vara.Application/Vara.Application.csproj`
  and `src/Vara.Infrastructure/Vara.Infrastructure.csproj`. Verify `dotnet build
  tests/Vara.IntegrationTests` succeeds.
- [ ] 1.2 Add `<Project Path="tests/Vara.IntegrationTests/Vara.IntegrationTests.csproj" />` to
  `Vara.slnx`, alongside the other three test projects. Verify `dotnet test` run from the repo
  root discovers and attempts to run the new project (even with zero tests present yet).

## 2. Minimal test doubles for the non-manifest, non-content-store ports

- [ ] 2.1 In a new `tests/Vara.IntegrationTests/Backup/Fakes.cs`, add small, self-contained
  `FakeContentStore` (needed only for the `BackupPipeline` tests - see design.md's Decisions on
  why `PruneService`/`SnapshotHistoryService` use the real content store instead),
  `FakeFileSystemScanner`, `FakeRunLock`, and `FakeHasher` implementations (copied from
  `tests/Vara.Application.Tests/Backup/Fakes.cs`, trimmed to only the members `BackupPipeline`
  and its collaborators require) - per design.md's decision to duplicate these small fakes
  rather than add a project reference to another test project. Verify the project still builds.

## 3. BackupPipeline against the real repository

- [ ] 3.1 In a new `tests/Vara.IntegrationTests/Backup/BackupPipelineRealRepositoryTests.cs`, add
  a test that runs `BackupPipeline` with a real `SqliteSnapshotRepository` (backed by a temp
  `.db` file, disposed and deleted per test - mirroring
  `SqliteSnapshotRepositoryTests`'s setup/teardown pattern) and the fakes from task 2.1, adding a
  new source file, and asserts the run completes successfully and the file is reflected in
  `GetCurrentState()`. Verify the test passes.
- [ ] 3.2 In the same file, add a test that runs the pipeline twice against an unchanged source
  file (same real repository and content store carried across both runs, a fresh `FakeRunLock`
  per run) and asserts the second run transfers zero bytes and adds zero files - the real-repository
  counterpart to `BackupPipelineTests.Running_twice_against_an_unchanged_source_performs_zero_content_transfer_the_second_time`.
  Verify the test passes.
- [ ] 3.3 In the same file, add a test that runs the pipeline, then renames the source file
  changing only its casing (content and modified time unchanged), runs the pipeline again, and
  asserts: zero bytes transferred and zero files added on the second run, and the real
  repository's `GetCurrentState()` still reports exactly one entry for the path afterward - the
  real-repository counterpart to
  `BackupPipelineTests.Running_twice_after_a_source_files_casing_changes_treats_it_as_unchanged_not_as_a_new_addition`
  from the `fix-current-state-case-sensitivity` change, this time exercising the real
  `SqliteSnapshotRepository` instead of `FakeSnapshotRepository`. Verify the test passes.

## 4. PruneService against the real repository and real content store

- [ ] 4.1 In a new `tests/Vara.IntegrationTests/Retention/PruneServiceRealPortsTests.cs`, add a
  test using a real `SqliteSnapshotRepository` (temp `.db` file) and a real
  `FileSystemContentStore` (temp target-root directory), modeled on
  `PruneServiceTests.Prune_removes_eligible_snapshots_and_garbage_collects_unreferenced_content`:
  seed content via `StoreFromStream`/`PlaceAtMirrorPath`, record snapshots directly against the
  real repository, run `Prune`, and assert both that the expired snapshot's manifest rows are
  gone from the real repository and that the unreferenced blob is actually deleted from disk
  (e.g. `File.Exists` against the blob's real path is false, or `HasContent` returns false).
  Verify the test passes.
- [ ] 4.2 In the same file, add a test modeled on
  `PruneServiceTests.Prune_never_touches_files_currently_present_in_the_live_mirror`, asserting
  that after pruning, the file placed via `PlaceAtMirrorPath` still exists at its real mirror
  path on disk with its original content, even though its earlier (superseded) version's
  snapshot was eligible for removal. Verify the test passes.
- [ ] 4.3 In the same file, add a test modeled on
  `PruneServiceTests.Prune_never_removes_the_most_recent_completed_snapshot_even_with_an_all_zero_tier_policy`,
  asserting the same newest-snapshot-survives guarantee holds against the real repository.
  Verify the test passes.

## 5. SnapshotHistoryService against the real repository and real content store

- [ ] 5.1 In a new `tests/Vara.IntegrationTests/History/SnapshotHistoryServiceRealPortsTests.cs`,
  add a test using a real `SqliteSnapshotRepository` and a real `FileSystemContentStore`,
  modeled on `SnapshotHistoryServiceTests.RestoreAsOf_extracts_the_content_current_at_that_date`:
  store real content, record a file version against the real repository, call `RestoreAsOf`, and
  assert the real destination file on disk contains the expected bytes. Verify the test passes.
- [ ] 5.2 In the same file, add a test modeled on the mirror-containment guard (see
  `SnapshotHistoryServiceTests` for the exact scenario name/shape), asserting that
  `RestoreAsOf`/`RestoreVersion` refuses to write into a destination that
  `FileSystemContentStore.IsWithinMirror` (the real path-resolution logic, not the fake's
  `MirrorPaths` set) reports as inside the live mirror. Verify the test passes.
- [ ] 5.3 In the same file, add a test asserting the existing-destination/overwrite behavior
  (destination already exists, `overwrite: false` throws; `overwrite: true` succeeds) against a
  real file on disk, using `FileSystemContentStore.TargetExists`'s real `File.Exists` check
  rather than the fake's tracked set. Verify the test passes.

## 6. Full verification

- [ ] 6.1 Run the full solution test suite (`dotnet test`) and verify all tests across all four
  test projects pass, confirming the new project integrates cleanly alongside the existing ones.
