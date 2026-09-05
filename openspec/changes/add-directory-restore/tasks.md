## 1. Content store: arbitrary-destination removal primitive

- [ ] 1.1 Add `void RemoveExtractedFile(string absolutePath)` to `IContentStore`
      (`Vara.Core/Abstractions/IContentStore.cs`), documented as a no-op when the file is
      already absent, mirroring `ExtractTo`'s arbitrary-destination-outside-the-mirror nature.
      Verify the interface compiles and every implementer (including test fakes) is updated.
- [ ] 1.2 Implement `RemoveExtractedFile` in `FileSystemContentStore`
      (`Vara.Infrastructure/Storage/FileSystemContentStore.cs`). Verify with a
      `Vara.Infrastructure.Tests` test deleting an existing file and a test calling it on a
      path that does not exist (no exception).

## 2. Application layer: directory restore planning and execution

- [ ] 2.1 Add `DirectoryRestoreEntry` and `DirectoryRestorePlan` records to
      `Vara.Application/History/SnapshotHistoryService.cs` (or a new file in the same
      namespace), matching design.md's shape (`ToWrite`, `ToRemove`, `TotalBytes`).
- [ ] 2.2 Generalize `SnapshotHistoryService`'s existing directory-prefix helpers
      (`NormalizeDirectoryPrefix`, and a new descendant-matching helper alongside the existing
      immediate-child-only `TryGetImmediateChild`) so both `ListDirectory` and the new directory
      restore can share the prefix-normalization logic. Verify with unit tests covering prefix
      matching for nested paths, not just immediate children.
- [ ] 2.3 Implement `SnapshotHistoryService.PlanDirectoryRestore(directoryPath, asOf, outRoot,
      inPlace)`: resolve the historical set `S` (`GetStateAsOf`/`GetCurrentState`) and current
      set `C` (`GetCurrentState`) filtered to the directory's prefix, compute `ToWrite` from `S`
      and `ToRemove` from `C \ S` (only entries where `contentStore.TargetExists` is true),
      compute each entry's destination per design.md's out-root-relative and in-place mapping
      rules, and validate every destination via `contentStore.IsWithinMirror` up front, throwing
      `RestoreDestinationInMirrorException` on any violation before returning a plan. Throw
      `NoSuchDirectoryException` when the directory was never tracked (reusing the same
      any-tracked check `ListDirectory` already performs, including tombstones). Verify with
      `Vara.Application.Tests` covering: files restored from history, deleted-then-restored
      files, files excluded because added after `asOf`, computed removals, in-place path
      mapping, mirror-containment rejection, and the never-tracked-directory error.
- [ ] 2.4 Implement `SnapshotHistoryService.ExecuteDirectoryRestore(plan, onBytesCopied,
      onSizeResolved)`: call `onSizeResolved(plan.TotalBytes)` once, run the removal pass
      (`contentStore.RemoveExtractedFile` for each `ToRemove` entry) to completion, then write
      each `ToWrite` entry (creating parent directories as needed, then `contentStore.ExtractTo`
      forwarding `onBytesCopied`). Verify with `Vara.Application.Tests` asserting removals happen
      before writes, destination content matches the plan, and the cumulative bytes reported to
      `onBytesCopied` sum to `TotalBytes`.

## 3. CLI: `--recursive` restore

- [ ] 3.1 Add a `--recursive` boolean option to `RestoreCommand`
      (`Vara.Cli/Commands/RestoreCommand.cs`), mutually exclusive with `--version` (report a
      clear error and perform no restore if both are given, matching the existing
      `--out`/`--in-place` mutual-exclusion error style). Verify with a
      `Vara.Cli.Tests` test asserting the error message and exit code.
- [ ] 3.2 Wire `--recursive` to call `PlanDirectoryRestore`/`ExecuteDirectoryRestore` instead of
      the existing single-file `RestoreAsOf`/`RestoreVersion` path, reusing the same
      `SnapshotPathResolver.TryResolve` flexible path resolution already used for the single-file
      path argument. Verify by exercising the command end-to-end in
      `Vara.Cli.Tests`/`Vara.IntegrationTests` against a directory with a mix of live, deleted,
      and not-yet-existing-at-`asOf` files.
- [ ] 3.3 Implement the single confirmation prompt: reporting `plan.ToWrite.Count` and
      `plan.ToRemove.Count`, defaulting to not proceeding, honoring `--force` to skip it, and
      reporting a clear non-interactive error when neither `--force` nor an interactive session
      is available - mirroring the existing single-file overwrite-prompt's shape
      (`StandardError.Console.Confirm`, `cancelled` handling in `RestoreCommand`). Verify with
      `Vara.Cli.Tests` covering: interactive confirm/decline, `--force` skip, and the
      non-interactive-without-`--force` error.
- [ ] 3.4 Wire progress reporting: construct one `RestoreProgressReporter` per directory
      restore, call `OnSizeResolved` once via `ExecuteDirectoryRestore`'s `onSizeResolved`
      callback, and reuse the existing live-display (`RunWithLiveDisplay`-equivalent) and
      plain-output (`RunWithPlainOutput`-equivalent) rendering paths already used for single-file
      restore. Verify by confirming (via existing `RestoreProgressReporterTests`-style unit
      tests, extended if needed) that bytes reported across multiple sequential `ExtractTo`
      calls accumulate correctly against the single aggregate total.
- [ ] 3.5 Update `RestoreCommand`'s option/argument help text (`path`, new `--recursive`) to
      describe the directory-restore mode. Verify by running `vara restore --help` and
      confirming the new flag and updated `path` description appear.

## 4. Error handling and cross-cutting checks

- [ ] 4.1 Confirm `ErrorReporting.cs`'s friendly-message switch already covers
      `RestoreDestinationInMirrorException` and `NoSuchDirectoryException` for the new
      recursive-restore call sites (both already exist for single-file restore and `browse`
      respectively) - add a friendly message only if a gap is found. Verify by triggering each
      error path via the CLI and confirming a friendly `Error: ...` message rather than a stack
      trace.

## 5. End-to-end verification

- [ ] 5.1 Add an integration test (`Vara.IntegrationTests`) that backs up a directory across
      multiple snapshots (adding, modifying, and deleting files along the way), then performs a
      recursive `restore --at <date>` to a fresh `--out` destination and asserts the destination
      tree exactly matches the historical state (including a since-deleted file's return and a
      later-added file's exclusion).
- [ ] 5.2 Add an integration test for `--recursive --in-place` asserting removal of a
      currently-live file that did not exist as of the requested date, and correct per-file
      destination mapping when the restored subtree spans more than one configured source.
- [ ] 5.3 Run the full test suite (`dotnet test` from `src`/repo root, per the project's existing
      test command) and confirm all tests pass, including the new and existing restore-related
      tests.
