## Why

`Vara.Application.Tests` currently exercises its cross-port orchestration logic - `BackupPipeline`,
`PruneService`, and `SnapshotHistoryService` - only against hand-maintained fakes for the
`ISnapshotRepository` and `IContentStore` ports (`Fakes.cs`). This came to a head during the
`fix-current-state-case-sensitivity` change: the real `SqliteSnapshotRepository.GetCurrentState()`
had a case-sensitivity bug, and its fake counterpart, `FakeSnapshotRepository.GetCurrentState()`,
had independently duplicated the exact same bug. A pipeline-level regression test added at the
time could only be made meaningful by also patching the fake to match the real fix - but nothing
structurally prevents the two from drifting apart again the next time either one changes.

A solution-wide review confirms this isn't unique to `BackupPipeline`. Every orchestrator in
`Vara.Application` that combines multiple ports whose real implementations live in
`Vara.Infrastructure` is exposed to the same class of risk, and two of them re-implement
non-trivial logic in their fakes that could just as easily drift:
- `PruneService` (`ISnapshotRepository` + `IContentStore` + `IRunLock`) - `FakeSnapshotRepository`
  re-implements the "protect the current row per path" pruning-eligibility logic, and
  `FakeContentStore` re-implements mirror/blob semantics. This is the exact mechanism (garbage
  collection) that would have permanently leaked a blob under the original bug, making it
  arguably more consequential than `BackupPipeline` to cover with a real repository.
- `SnapshotHistoryService` (`ISnapshotRepository` + `IContentStore`) - restore operations rely on
  `FakeContentStore`'s independent re-implementation of mirror-containment checks
  (`IsWithinMirror`) and destination-existence checks (`TargetExists`), which could diverge from
  `FileSystemContentStore`'s real path-resolution logic.

By contrast, `ProfileResolver`'s only dependency, `IProfileConfigLoader`, is stubbed in its tests
by a fake (`StubConfigLoader`) that has no re-implemented logic at all - it only returns a preset
list - so there is no meaningful drift risk to close there, and it is intentionally out of scope
for this change. Similarly, the `Reporting` classes and `RetentionEvaluator` depend on no ports at
all (pure computation over already-loaded data) and have nothing to test cross-layer.

## What Changes

- Add a new test project, `tests/Vara.IntegrationTests`, that can run application-layer
  orchestrators against real infrastructure-layer port implementations instead of fakes.
- Cover three orchestrators against real ports:
  - `BackupPipeline` against the real `SqliteSnapshotRepository` (fakes remain for
    `IContentStore`/`IFileSystemScanner` here - see design.md for why).
  - `PruneService` against the real `SqliteSnapshotRepository` **and** the real
    `FileSystemContentStore`, verifying that pruning and garbage collection actually remove
    manifest rows and blobs on disk, and never disturb the live mirror.
  - `SnapshotHistoryService` against the real `SqliteSnapshotRepository` **and** the real
    `FileSystemContentStore`, verifying that restore actually extracts real file content,
    refuses to write into the real live mirror, and honors overwrite rules against real files.
- Existing unit-level tests that already use fakes for fast, isolated testing of orchestration
  logic (`BackupPipelineTests`, `BackupExecutorTests`, `BackupDifferTests`, `PruneServiceTests`,
  `SnapshotHistoryServiceTests`, etc.) are unaffected and continue to use fakes; this change adds
  a complementary layer of coverage, it does not replace the existing fakes or the tests that
  rely on them.
- `ProfileResolver`/`IProfileConfigLoader` and the pure-computation `Reporting`/
  `RetentionEvaluator` classes are explicitly out of scope - see Why for the reasoning.
- No production code changes. This is entirely test-project structure and new test coverage.

## Capabilities

### New Capabilities

(none)

### Modified Capabilities

(none - this change is pure test infrastructure; no spec-level behavior changes. `skip_specs:
true` is set in this change's `.openspec.yaml`.)

## Impact

- **Test projects**: new `tests/Vara.IntegrationTests` project, referencing `Vara.Application`
  and `Vara.Infrastructure` - see design.md for the structural options considered and the
  recommended approach.
- **Test code**: new test files covering `BackupPipeline`, `PruneService`, and
  `SnapshotHistoryService` runs against real infrastructure-layer port implementations; no
  changes to existing fakes or existing tests are required by this change itself.
- **No production code, schema, or CLI-facing changes.**

