## Context

See proposal.md - Why for the motivating gap and the solution-wide review of candidates. Key
points from the existing test layout and the infrastructure implementations that shape the
approach:

- Each of the three current test projects (`Vara.Core.Tests`, `Vara.Application.Tests`,
  `Vara.Infrastructure.Tests`) references exactly one production project and uses fakes for
  every port it depends on that lives in a different layer (`tests/Vara.Application.Tests/Backup/Fakes.cs`
  implements `ISnapshotRepository` and `IContentStore` entirely in-memory, re-implementing
  meaningful logic such as `GetCurrentState`'s per-path grouping, `PruneSnapshots`'s
  current-row-protection rule, and `IsWithinMirror`'s containment check).
- `Vara.slnx` lists each project explicitly (no wildcard glob), so `dotnet test` run at the repo
  root already covers every listed project without extra CI wiring; adding a project only
  requires one new `<Project Path="..." />` line.
- Central package management (`Directory.Packages.props`) already pins `xunit`,
  `xunit.runner.visualstudio`, and `Microsoft.NET.Test.Sdk` versions, so a new test project needs
  no new `PackageVersion` entries - only the same three unversioned `PackageReference`s every
  other test project already uses.
- `FileSystemContentStore`'s hardlink-related complexity (`ProbeHardlinkSupport`,
  `SupportsHardlinks`, the per-placement hardlink-then-copy-fallback in `PlaceAtMirrorPath`) is
  only reachable through `PlaceAtMirrorPath` and the `SupportsHardlinks` getter. Neither
  `PruneService` nor `SnapshotHistoryService` ever calls `PlaceAtMirrorPath` or reads
  `SupportsHardlinks` - they only use `StoreFromStream`, `ListAllStoredHashes`, `DeleteContent`,
  `HasContent` (Prune) and `ExtractTo`, `IsWithinMirror`, `TargetExists` (History), none of which
  touch hardlink logic. This matters for the per-orchestrator decisions below.

## Goals / Non-Goals

**Goals:**
- Let `BackupPipeline`, `PruneService`, and `SnapshotHistoryService` each be driven, in at least
  one test, against the real `SqliteSnapshotRepository` (and, for the latter two, the real
  `FileSystemContentStore`) instead of fakes - so a bug affecting both a real port and its fake
  can't be masked by the fake alone.
- Keep the change small and easy to run - reuse the existing `dotnet test` entry point with no
  new tooling, filters, or CI configuration required.
- Preserve the existing per-layer unit-test/fakes convention for all current tests; this change
  is additive.

**Non-Goals:**
- Replacing or rewriting existing fakes or fake-based tests. `FakeSnapshotRepository`,
  `FakeContentStore`, etc. remain the right tool for fast, isolated orchestration-logic tests,
  and `PruneServiceTests`/`SnapshotHistoryServiceTests`/`BackupPipelineTests` etc. are unchanged.
  This change adds a second, smaller layer of tests alongside them.
- Covering every port with a real implementation, or auditing every application-layer class for
  cross-layer gaps beyond the three orchestrators identified. `ProfileResolver` is explicitly
  out of scope entirely: its fake (`StubConfigLoader`) has no re-implemented logic to drift,
  and the `Reporting`/`RetentionEvaluator` classes depend on no ports at all - see proposal.md -
  Why for why these were evaluated and excluded.
- Covering `IFileSystemScanner` with a real filesystem-scanning implementation for
  `BackupPipeline`, or exercising `FileSystemContentStore`'s hardlink path
  (`PlaceAtMirrorPath`/`ProbeHardlinkSupport`) at all. Not required to close the specific gaps
  this change addresses (see Decisions).
- Any change to production code, CI infrastructure, or the solution's build configuration beyond
  registering the new test project.

## Decisions

- **Create a new dedicated test project, `tests/Vara.IntegrationTests`, referencing both
  `Vara.Application` and `Vara.Infrastructure`, rather than adding a `Vara.Infrastructure`
  reference directly to `Vara.Application.Tests`.**
  - Alternative considered: add the `Vara.Infrastructure` project reference straight to
    `Vara.Application.Tests.csproj` and place the new tests alongside the existing
    `BackupPipelineTests.cs`/`PruneServiceTests.cs`/`SnapshotHistoryServiceTests.cs`. Rejected -
    every existing test project in this repo maps to exactly one production layer and uses
    fakes for its dependencies; blending a second production layer into `Vara.Application.Tests`
    breaks that one-project-one-layer convention and makes it harder to tell, from the project
    reference list alone, whether a given test in that project is a fast fake-based unit test or
    a slower real-infrastructure test. A separate project keeps that distinction structural (by
    project) rather than left to naming or comments within a single project.
  - The new project follows the same conventions as the existing test projects: same
    `TargetFramework`/`Nullable` settings via `Directory.Build.props`, same three
    `PackageReference`s (`Microsoft.NET.Test.Sdk`, `xunit`, `xunit.runner.visualstudio`), and is
    added to `Vara.slnx` exactly like the other three.
- **`BackupPipeline`: real `SqliteSnapshotRepository`, fakes for `IContentStore`/
  `IFileSystemScanner`.** This is the port whose fake counterpart most recently duplicated a
  real bug (`fix-current-state-case-sensitivity`). Swapping in the real `FileSystemContentStore`
  here as well was considered and rejected for this pipeline specifically: `BackupPipeline` does
  call `ProbeHardlinkSupport`/`PlaceAtMirrorPath`, which would pull in hardlink-support probing
  and real filesystem mirroring semantics that are orthogonal to the bug class this change
  exists to catch (manifest/lookup correctness), adding setup complexity and flakiness risk
  (hardlink support varies by host filesystem) without a corresponding, currently-known gap in
  test coverage for that path.
- **`PruneService` and `SnapshotHistoryService`: real `SqliteSnapshotRepository` *and* real
  `FileSystemContentStore`.** Unlike `BackupPipeline`, neither of these orchestrators touches
  `FileSystemContentStore`'s hardlink logic (see Context), so there is no corresponding
  flakiness risk to weigh against the benefit - and the benefit is larger here, because the
  fakes' re-implemented logic (`FakeSnapshotRepository`'s prune-protection rule,
  `FakeContentStore`'s `IsWithinMirror`/mirror-dictionary semantics) is exactly what a real
  disk-backed store could disagree with. `PruneService` in particular performs an irreversible,
  destructive operation (blob deletion); proving that a real repository's `PruneSnapshots`/
  `GetAllReferencedContentHashes` and a real content store's `ListAllStoredHashes`/
  `DeleteContent` agree on what's safe to remove is the highest-value target in this change.
- **`ProfileResolver` is out of scope.** Its only fake, `StubConfigLoader`, contains no
  re-implemented parsing/validation logic - it is a plain in-memory list return - so there is no
  drift risk for a cross-layer test to close. Revisit only if `StubConfigLoader` ever grows real
  logic of its own.
- **Reuse the existing `BackupPipelineTests`/`PruneServiceTests`/`SnapshotHistoryServiceTests`
  test shape (temp directories, manual construction of profiles/entries, `FakeRunLock`) rather
  than inventing new test infrastructure.** The new project duplicates the small fakes it still
  needs (`FakeFileSystemScanner`, `FakeRunLock`, `FakeHasher`, and `FakeContentStore` only for
  the `BackupPipeline` tests) instead of adding a project reference from the new project to
  another test project, keeping test-project dependencies one-directional and simple. Real
  `SqliteSnapshotRepository`/`FileSystemContentStore` instances are constructed directly, backed
  by a per-test temp directory/file, matching `SqliteSnapshotRepositoryTests`'s existing
  create-dispose-delete pattern.

## Risks / Trade-offs

- [A fourth test project adds a small amount of solution/build overhead (one more project to
  restore/build/test).] -> Mitigation: the project is intentionally scoped to three
  orchestrators and reuses existing package versions and build settings, so the marginal
  overhead is small relative to the value of closing the fake/real drift gap.
- [Duplicating small fakes (`FakeContentStore` for the `BackupPipeline` tests,
  `FakeFileSystemScanner`, `FakeRunLock`, `FakeHasher`) into the new project instead of sharing
  them introduces its own, smaller-scale duplication risk.] -> Mitigation: these fakes are
  simple, stable, and (for `FakeRunLock`/`FakeFileSystemScanner`/`FakeHasher`) not implicated in
  any known drift bug; if this becomes a recurring pain point, a future change can extract a
  small shared test-support project.
- [SQLite- and filesystem-backed tests are slower and touch disk (temp `.db` files and temp
  target-root directories with real mirror/blob subfolders), unlike the in-memory fakes.] ->
  Mitigation: `SqliteSnapshotRepositoryTests` already does the SQLite half of this today in
  `Vara.Infrastructure.Tests` with acceptable performance (sub-second per test), and
  `BackupExecutorTests`/`BackupPipelineTests` already do real temp-directory file I/O today in
  `Vara.Application.Tests`; the new project follows the same per-test temp-path,
  dispose-and-delete pattern already established in both.
- [Using the real `FileSystemContentStore` for `PruneService`/`SnapshotHistoryService` but not
  for `BackupPipeline` is an asymmetric rule that could look inconsistent at a glance.] ->
  Mitigation: the asymmetry is deliberate and documented above (hardlink-path reachability
  differs per orchestrator); each orchestrator's test file can carry a short comment pointing
  back to this design decision.

## Open Questions

(none)

