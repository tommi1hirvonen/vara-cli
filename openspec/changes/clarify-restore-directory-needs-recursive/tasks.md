## 1. Exception type

- [ ] 1.1 Add `RestoreTargetIsDirectoryException(string relativePath)` to
  `src/Vara.Core/Snapshots/SnapshotExceptions.cs`, distinct from `NoHistoryForPathException`,
  with a message stating the path is a directory and that `--recursive` is required. Verify it
  builds and follows the existing exception doc-comment style in that file.

## 2. Single-file restore detection

- [ ] 2.1 In `src/Vara.Cli/Commands/RestoreCommand.cs`, change the existing `IsDirectoryTracked`
  helper's visibility from `private` to `internal` (no behavior change) and verify existing
  `RunRecursiveRestore` callers/tests still pass unchanged.
- [ ] 2.2 In the single-file restore branch, after `SnapshotPathResolver.TryResolve` fails to
  resolve `path` to a tracked file (i.e. falls back to the raw input), check
  `IsDirectoryTracked(history, path)`; if true, throw `RestoreTargetIsDirectoryException(path)`
  before reaching the version-picker/`GetFileHistory` lookup that currently throws
  `NoHistoryForPathException`. Verify a manual repro (`restore <tracked-dir>` without
  `--recursive`) now reports the new message instead of "No history exists for path".
- [ ] 2.3 Confirm `ErrorReporting` (or wherever exceptions are surfaced to exit codes/messages)
  requires no change for the new exception type - verify it renders via the same generic
  `Exception.Message` path other `Snapshot*Exception` types already use.

## 3. Tests

- [ ] 3.1 Add a `RestoreCommandTests` case: restoring a tracked directory path without
  `--recursive` reports the new directory-specific error and exits non-zero, verified by
  asserting on the printed error text and exit code.
- [ ] 3.2 Add a `RestoreCommandTests` case confirming a path that matches neither a tracked file
  nor a tracked directory still reports the original "No history exists for path" message
  (regression guard for the Non-Goal in proposal.md).
- [ ] 3.3 Run `dotnet test` for `Vara.Cli.Tests` (and `Vara.Core.Tests` if the new exception type
  needs direct coverage) and verify all tests pass.

## 4. Spec sync

- [ ] 4.1 Verify `openspec validate --strict` passes for this change before archiving.
