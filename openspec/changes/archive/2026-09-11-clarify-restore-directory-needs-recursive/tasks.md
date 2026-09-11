## 1. Exception type

- [x] 1.1 Add `RestoreTargetIsDirectoryException(string relativePath)` to
  `src/Vara.Core/Snapshots/SnapshotExceptions.cs`, distinct from `NoHistoryForPathException`,
  with a message stating the path is a directory and that `--recursive` is required. Verify it
  builds and follows the existing exception doc-comment style in that file.

## 2. Single-file restore detection

- [x] 2.1 In `src/Vara.Cli/Commands/RestoreCommand.cs`, change the existing `IsDirectoryTracked`
  helper's visibility from `private` to `internal` (no behavior change) and verify existing
  `RunRecursiveRestore` callers/tests still pass unchanged.
- [x] 2.2 In the single-file restore branch, after `SnapshotPathResolver.TryResolve` fails to
  resolve `path` to a tracked file (i.e. falls back to the raw input), re-run
  `SnapshotPathResolver.TryResolve` with `IsDirectoryTracked(history, candidate)` as its
  `hasHistory` predicate (same flexible-path resolution order, not just a literal check on the
  raw input); if it resolves, throw `RestoreTargetIsDirectoryException(resolvedDirectoryPath)`
  before reaching the version-picker/`GetFileHistory` lookup that currently throws
  `NoHistoryForPathException`. Verify a manual repro (`restore <tracked-dir>` without
  `--recursive`, run from inside the profile's source tree) now reports the new message instead
  of "No history exists for path".
- [x] 2.3 Confirm `ErrorReporting` requires no change for the new exception type - it did:
  `RestoreTargetIsDirectoryException` had to be added to `ErrorReporting`'s
  `TryGetFriendlyMessage` switch (alongside the other `Snapshot*Exception` types) so it renders
  as a clean `Error: ...` message instead of falling through to the generic unhandled-exception/
  stack-trace path.

## 3. Tests

- [x] 3.1 Add a `RestoreCommandTests` case: restoring a tracked directory path without
  `--recursive` fails with a hard error, and a `RestoreTargetIsDirectoryException`-specific
  friendly-message mapping test in `ErrorReportingTests` (the established convention for
  asserting exact error text per exception type, since `RestoreCommand` has no injectable error
  console).
- [x] 3.2 Add a `RestoreCommandTests` case confirming a path that matches neither a tracked file
  nor a tracked directory still fails with a hard error (regression guard for the Non-Goal in
  proposal.md).
- [x] 3.3 Run `dotnet test` for `Vara.Cli.Tests` (and `Vara.Core.Tests` if the new exception type
  needs direct coverage) and verify all tests pass.

## 4. Spec sync

- [x] 4.1 Verify `openspec validate --strict` passes for this change before archiving.
