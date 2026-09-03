## 1. Path resolution foundation

- [ ] 1.1 Make `AbsolutePathMirrorMapper` (`src/Vara.Infrastructure/FileSystem/
      AbsolutePathMirrorMapper.cs`) non-`internal` and add `FromMirrorPath(string
      mirrorRelativePath)`, reversing `ToMirrorPath` by re-inserting `:` after a single-character
      leading path segment (returning the input unchanged if it doesn't have that shape). Verify
      the solution builds and add round-trip unit tests (`ToMirrorPath` then `FromMirrorPath`
      returns the original) in a new `tests/Vara.Infrastructure.Tests/FileSystem/
      AbsolutePathMirrorMapperTests.cs`.
- [ ] 1.2 Add a new `SnapshotPathResolver` in `src/Vara.Application/History/` implementing the
      three-step priority chain from the `snapshot-history` delta's "Flexible path input"
      requirement: (1) literal match against a caller-supplied `Func<string, bool>` "has history"
      predicate, (2) cwd-resolved absolute path stripped of the mirror root when it falls inside
      the mirror, (3) cwd-resolved absolute path converted via `AbsolutePathMirrorMapper.
      ToMirrorPath` otherwise. Verify with new unit tests in `tests/Vara.Application.Tests/
      History/SnapshotPathResolverTests.cs` covering all four accepted input forms plus the
      no-match case.
- [ ] 1.3 Wire `HistoryCommand` and `RestoreCommand` (`src/Vara.Cli/Commands/`) to resolve their
      `path` argument through `SnapshotPathResolver` before calling into
      `SnapshotHistoryService`, using `repository.GetFileHistory(candidate).Count > 0` as the
      "has history" predicate. Verify manually: run `history`/`restore` with a mirror-relative
      path (existing behavior unchanged), a path relative to a working directory inside the
      mirror, and an absolute source path, confirming all three resolve to the same file.

## 2. Profile auto-detection

- [ ] 2.1 Add a working-directory resolution path to `ProfileResolver`
      (`src/Vara.Application/Profiles/ProfileResolver.cs`): when no profile name is given, walk
      upward from `Directory.GetCurrentDirectory()` looking for a `.vara\profile.db` file; if
      found, construct a synthetic `Profile` (name derived from that directory's folder name,
      target root set to that directory, a single placeholder `Source` pointing at the same
      directory since `Profile`'s constructor requires at least one, no retention/concurrency)
      without reading `~/.vara/profiles.yml`. If no name is given and no such file is found by
      the filesystem root, throw a new clear exception requiring a profile name. Verify with new
      unit tests in `tests/Vara.Application.Tests/Profiles/ProfileResolverTests.cs` covering: an
      explicit name still resolves via config exactly as before, a working directory inside a
      directory containing `.vara\profile.db` resolves with no config file present, and a working
      directory outside any such directory throws the new clear error.
- [ ] 2.2 Update `HistoryCommand`, `RestoreCommand`, and the new `BrowseCommand`/
      `DeletedCommand`/`ShowCommand`/`DiffCommand` (added in section 6) to make the `profile`
      argument optional and call the new resolution path instead of requiring a name up front.
      Verify manually: run `history`/`restore` with the working directory inside a profile's
      target root and no profile argument, confirming it resolves without
      `~/.vara/profiles.yml` existing.
- [ ] 2.3 Register the new "profile name required" exception in `ErrorReporting.cs`'s
      friendly-message switch (`src/Vara.Cli/Composition/ErrorReporting.cs`). Verify by running a
      browsing command with the working directory outside any mirror and no profile argument,
      confirming a friendly `Error: ...` message rather than a stack trace.

## 3. Repository: point-in-time and tombstone queries

- [ ] 3.1 Add `IReadOnlyDictionary<string, CurrentFileState> GetStateAsOf(DateTimeOffset asOf)`
      to `ISnapshotRepository` (`src/Vara.Core/Abstractions/ISnapshotRepository.cs`) and
      implement it in `SqliteSnapshotRepository`
      (`src/Vara.Infrastructure/Snapshots/SqliteSnapshotRepository.cs`) by adapting
      `GetCurrentState`'s `MAX(id)`-per-path query with an added `recorded_at <= @asOf` bound
      inside the inner grouping. Verify with new tests in
      `tests/Vara.Infrastructure.Tests/Snapshots/SqliteSnapshotRepositoryTests.cs` covering: a
      path added after `asOf` (excluded), a path changed both before and after `asOf` (returns
      the pre-`asOf` version), and a path deleted before `asOf` (excluded).
- [ ] 3.2 Add `IReadOnlyList<FileVersionRecord> GetTombstones(DateTimeOffset? asOf)` to
      `ISnapshotRepository`/`SqliteSnapshotRepository`: latest row per `relative_path` where that
      row's `change_kind = Deleted`, optionally bounded by `recorded_at <= asOf` the same way as
      3.1. Verify with new tests covering: a deleted path with no bound, a deleted path excluded
      when `asOf` predates its deletion, and a path excluded once it is live again after being
      re-added post-deletion.
- [ ] 3.3 Add `IReadOnlyDictionary<string, string> GetMoveOrigins()` to
      `ISnapshotRepository`/`SqliteSnapshotRepository`, mapping each currently-live path whose
      latest record is `Moved` back to its `previous_relative_path`, used to distinguish "moved
      out" from "deleted" per design.md's "Moved-out detection" decision. Verify with a new test
      covering a path moved from A to B, confirming the map resolves A to B while B's own current
      state is unaffected.

## 4. Content store: readable blob stream

- [ ] 4.1 Add `Stream OpenRead(string hash)` to `IContentStore`
      (`src/Vara.Core/Abstractions/IContentStore.cs`) and implement it in
      `FileSystemContentStore` (`src/Vara.Infrastructure/Storage/FileSystemContentStore.cs`) as a
      read-only `FileStream` over the blob path, throwing the same `FileNotFoundException` shape
      `ExtractTo` already throws for a missing hash. Verify with a new test in
      `tests/Vara.Infrastructure.Tests/Storage/FileSystemContentStoreTests.cs` covering a stored
      hash (readable, content matches) and a missing hash (throws).
- [ ] 4.2 Implement `OpenRead` on the in-memory `FakeContentStore`
      (`tests/Vara.Application.Tests/Backup/Fakes.cs`). Verify the solution builds and existing
      tests using `FakeContentStore` still pass.

## 5. Application layer: browsing, show, diff, in-place restore

- [ ] 5.1 Add `IReadOnlyList<DirectoryEntry> ListDirectory(string directoryPath, DateTimeOffset?
      asOf, bool includeDeleted)` to `SnapshotHistoryService`
      (`src/Vara.Application/History/SnapshotHistoryService.cs`): sources from
      `GetCurrentState()`/`GetStateAsOf`, optionally unions `GetTombstones`, filters to the
      one-level prefix under `directoryPath`, and synthesizes subdirectory entries in memory.
      Mark each entry Live/Deleted/Moved (via `GetMoveOrigins`) and File/Directory. Throw a new
      `NoSuchDirectoryException` when no tracked path, at any point in history, falls under the
      requested prefix. Verify with new unit tests in `tests/Vara.Application.Tests/History/
      SnapshotHistoryServiceTests.cs` covering: live-only listing, `asOf` listing, `includeDeleted`
      interleaving, a moved-out entry marked distinctly, a wholly-deleted subdirectory still
      appearing under `includeDeleted`, and the never-tracked-directory error.
- [ ] 5.2 Add `IReadOnlyList<FileVersionRecord> ListDeleted(string? directoryPath,
      DateTimeOffset? since)` to `SnapshotHistoryService`, built on `GetTombstones`, optionally
      prefix-filtered and date-filtered, sorted most-recently-deleted first. Verify with new unit
      tests covering whole-profile, subtree-scoped, and no-results cases.
- [ ] 5.3 Add `void ShowVersion(string relativePath, long? versionId, DateTimeOffset? asOf,
      Stream destination)` to `SnapshotHistoryService`, resolving the version the same way
      `RestoreVersion`/`RestoreAsOf` already do and copying its content via the new
      `IContentStore.OpenRead` instead of `ExtractTo`. Verify with new unit tests covering a
      resolved version's content copied byte-for-byte to the destination stream, and the existing
      `NoHistoryForPathException`/`NoMatchingVersionException` thrown for an unknown path/version.
- [ ] 5.4 Add a way to open two resolved versions' content for comparison (e.g. `(Stream Left,
      Stream Right) OpenVersionsForDiff(string relativePath, long? leftVersionId, DateTimeOffset?
      leftAsOf, long? rightVersionId, DateTimeOffset? rightAsOf)`) to `SnapshotHistoryService`,
      reusing the same version-resolution logic as 5.3 for each side. Verify with new unit tests
      covering two distinct versions resolving to their respective content, and the existing
      not-found exceptions surfacing for either side independently.

## 6. CLI: new and updated commands

- [ ] 6.1 Add `BrowseCommand` (`src/Vara.Cli/Commands/BrowseCommand.cs`) with an optional profile
      argument, a directory-path argument (resolved via `SnapshotPathResolver`), `--at <date>`,
      and `--deleted`, calling `SnapshotHistoryService.ListDirectory` and rendering via a new
      `DirectoryListingPresenter`. Verify `vara browse --help` lists the new options and a manual
      run against a test profile shows the expected rows.
- [ ] 6.2 Add `DeletedCommand` (`src/Vara.Cli/Commands/DeletedCommand.cs`) with an optional
      profile argument, an optional directory-scope argument, and `--since <date>`, calling
      `SnapshotHistoryService.ListDeleted` and rendering the results. Verify `vara deleted --help`
      and a manual run.
- [ ] 6.3 Add `ShowCommand` (`src/Vara.Cli/Commands/ShowCommand.cs`) with an optional profile
      argument, a path argument, and the existing `--at`/`--version` options, calling
      `SnapshotHistoryService.ShowVersion` with `Console.OpenStandardOutput()`. Verify `vara show
      --help` and a manual run against a known text version, confirming stdout contains exactly
      that version's content.
- [ ] 6.4 Add `DiffCommand` (`src/Vara.Cli/Commands/DiffCommand.cs`) with an optional profile
      argument, a path argument, and two independent version selectors (e.g. `--left-version`/
      `--left-at`, `--right-version`/`--right-at`), calling
      `SnapshotHistoryService.OpenVersionsForDiff` and rendering a unified textual diff. Verify
      `vara diff --help` and a manual run between two known versions of a text file shows the
      expected changed lines.
- [ ] 6.5 Update `RestoreCommand` (`src/Vara.Cli/Commands/RestoreCommand.cs`): make `--out`
      optional and add a mutually-exclusive `--in-place` flag, validated at parse time (clear
      error if both are given). When `--in-place` is set, compute the destination via
      `AbsolutePathMirrorMapper.FromMirrorPath` on the resolved path before proceeding through the
      existing overwrite-guard/prompt flow unchanged. Verify manually: restoring a deleted file
      with `--in-place` writes it back to its original location with no prompt, and restoring
      `--in-place` over an existing file prompts/requires `--force` exactly as `--out` does today.
- [ ] 6.6 Add the interactive version picker to `RestoreCommand`: when neither `--version` nor
      `--at` is given, neither `--out` nor `--in-place` selection has otherwise failed, and
      `OutputMode.IsLiveCapable(console)` is true, render the path's history via a
      `SelectionPrompt<FileVersionRecord>` (reusing `HistoryTablePresenter`'s per-row formatting
      for the prompt's choice labels) and proceed with the selected version; otherwise keep
      today's `--at`/`--version`-required error. Verify manually in an interactive terminal
      (arrow-select a version, confirm the right one restores) and confirm a redirected/
      non-interactive run still reports the existing clear error.
- [ ] 6.7 Register `BrowseCommand`, `DeletedCommand`, `ShowCommand`, `DiffCommand` in
      `src/Vara.Cli/Program.cs`. Verify `vara --help` lists all four new commands.

## 7. Presentation

- [ ] 7.1 Add `DirectoryListingPresenter` (`src/Vara.Cli/Presentation/
      DirectoryListingPresenter.cs`): an aligned table with columns for name, kind (File/
      Directory), status (Live/Deleted/Moved, colored consistently with
      `HistoryTablePresenter.ChangeKindStyle`, extracting a small shared color helper if that
      avoids duplicating the color switch), and size; deleted/moved rows interleaved with live
      rows, not grouped separately. Verify with a manual run showing a mix of live, deleted, and
      moved entries rendered distinctly.
- [ ] 7.2 Add rendering for `ListDeleted`'s results (a `DeletedReportPresenter`, or reusing
      `DirectoryListingPresenter`'s row rendering), sorted most-recently-deleted first, with the
      same empty-state message pattern as `HistoryTablePresenter`/`SnapshotsTablePresenter`
      ("No deleted files recorded..."). Verify with a manual run against a profile with no
      deleted files, confirming the empty-state message rather than an empty table.

## 8. Tests and verification

- [ ] 8.1 Add `BrowseCommandTests`/`DeletedCommandTests`/`ShowCommandTests`/`DiffCommandTests`,
      and update restore-related tests, under `tests/Vara.Cli.Tests/Commands/`, following the
      existing `BackupCommandTests.cs` style, covering option parsing and the mutually-exclusive
      `--out`/`--in-place` validation. Verify the new tests pass.
- [ ] 8.2 Add integration coverage under `tests/Vara.IntegrationTests/` exercising a real backup
      run followed by `browse --deleted`, `deleted`, `show`, `diff`, and `restore --in-place`
      against a real `SqliteSnapshotRepository`/`FileSystemContentStore`, following the existing
      `BackupPipelineRealRepositoryTests`/`BackupPipelineRealContentStoreTests` pattern. Verify
      the new tests pass.
- [ ] 8.3 Run `dotnet test` for the full solution and verify all tests pass, including every new
      case from sections 1-8.
- [ ] 8.4 Manually validate the end-to-end scenarios from proposal.md: browsing a directory with
      deleted entries, finding a deleted file profile-wide via `deleted`, restoring it with
      `--in-place` and no `--out`, and picking a version interactively when neither `--version`
      nor `--at` is given.
