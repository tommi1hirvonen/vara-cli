## 1. Domain exceptions

- [x] 1.1 Add `RestoreDestinationInMirrorException` and `DestinationExistsException` to
      `src/Vara.Core/Snapshots/SnapshotExceptions.cs`, following the existing style of
      `NoHistoryForPathException`/`NoMatchingVersionException` (relevant path(s) exposed as
      properties, a clear message), and verify the solution builds.
- [x] 1.2 Register both new exception types in `TryGetFriendlyMessage`'s switch in
      `src/Vara.Cli/Composition/ErrorReporting.cs`, and verify the solution builds.

## 2. Content store predicates

- [x] 2.1 Add `bool IsWithinMirror(string absolutePath)` and `bool TargetExists(string
      absolutePath)` to `IContentStore` (`src/Vara.Core/Abstractions/IContentStore.cs`) with doc
      comments describing their use by the snapshot-history capability's restore guards, and
      verify the solution builds (expect `FileSystemContentStore` and `FakeContentStore` to now
      show missing-member errors until sections 3 and 5 are done).
- [x] 2.2 Implement both members on `FileSystemContentStore`
      (`src/Vara.Infrastructure/Storage/FileSystemContentStore.cs`): `IsWithinMirror` resolves
      the input to a full path and checks it falls under the resolved `_mirrorRoot` (ordinal,
      case-insensitive, trailing-separator-safe prefix check); `TargetExists` is a thin
      `File.Exists` wrapper. Verify with a quick manual check (e.g. a scratch console call or
      the tests in 6.1) that a path like `<mirrorRoot>\..\other` is correctly excluded and a path
      like `<mirrorRoot>\sub\file.txt` is correctly included.

## 3. Restore guard logic

- [x] 3.1 Add an `overwrite` parameter (default `false`) to `SnapshotHistoryService.RestoreAsOf`
      and `RestoreVersion` (`src/Vara.Application/History/SnapshotHistoryService.cs`). Before
      calling `contentStore.ExtractTo`, throw `RestoreDestinationInMirrorException` if
      `contentStore.IsWithinMirror(destinationPath)` is true (checked unconditionally,
      regardless of `overwrite`), then throw `DestinationExistsException` if `overwrite` is
      `false` and `contentStore.TargetExists(destinationPath)` is true. Verify the solution
      builds and existing `SnapshotHistoryServiceTests` still pass unchanged (default `overwrite:
      false` preserves current calls that use a non-existent, non-mirror destination).
- [x] 3.2 Update the XML doc comments on `RestoreAsOf`/`RestoreVersion` to document the two new
      exceptions, and verify the solution builds.

## 4. CLI: `--force` flag and interactive confirmation

- [x] 4.1 Add a `--force` boolean option to `RestoreCommand`
      (`src/Vara.Cli/Commands/RestoreCommand.cs`), and pass `overwrite: true` to
      `RestoreAsOf`/`RestoreVersion` when it is set; verify `vara restore --help` lists the new
      option.
- [x] 4.2 When `--force` is not set, call the restore with `overwrite: false` first; on catching
      `DestinationExistsException`, branch on `Console.IsInputRedirected`: if `true`
      (non-interactive), rethrow so `ErrorReporting` reports the existing friendly error and the
      command exits with code 1; if `false` (interactive), write a confirmation prompt to
      `Console.Error` (e.g. `File '<out>' already exists. Overwrite? [y/N]: `) and read a line
      from stdin. Verify by manually running `restore` twice against the same existing `--out`
      path in an interactive terminal: once answering "y" (file is overwritten) and once
      answering "n" or pressing enter (file is left unchanged).
- [x] 4.3 If the user's answer to the prompt is affirmative (`y`/`yes`, case-insensitive),
      re-invoke the restore with `overwrite: true`. Otherwise, print a cancellation message (not
      prefixed `Error:`) and return `0` from the command action without modifying the
      destination. Verify manually that declining prints a clear cancellation message and `echo
      $LASTEXITCODE` (or `$?`) reports `0`.
- [x] 4.4 Verify manually that a restore whose `--out` resolves inside the profile's
      `TargetRoot` is refused with a clear `Error: ...` message even when `--force` is passed,
      confirming the mirror-containment guard is not force-able.

## 5. Test infrastructure

- [x] 5.1 Implement `IsWithinMirror` and `TargetExists` on the in-memory `FakeContentStore`
      (`tests/Vara.Application.Tests/Backup/Fakes.cs`): track a configurable mirror-root prefix
      (default empty/never-contains, settable per test) for `IsWithinMirror`, and back
      `TargetExists` with an in-memory set of "destination already has content" paths that tests
      can pre-populate. Verify the solution builds and all existing tests using
      `FakeContentStore` still pass.

## 6. Tests

- [x] 6.1 Add `FileSystemContentStoreTests` cases (`tests/Vara.Infrastructure.Tests/Storage/
      FileSystemContentStoreTests.cs`) covering `IsWithinMirror` for a path inside the mirror
      root, a path outside it, and a sibling directory whose name merely starts with the mirror
      root's name (to confirm the trailing-separator-safe comparison), plus `TargetExists` for
      an existing and a non-existing file.
- [x] 6.2 Add `SnapshotHistoryServiceTests` cases (`tests/Vara.Application.Tests/History/
      SnapshotHistoryServiceTests.cs`) covering: `RestoreAsOf`/`RestoreVersion` throw
      `RestoreDestinationInMirrorException` when the destination is within the mirror, even when
      `overwrite: true` is passed; throw `DestinationExistsException` when the destination
      exists and `overwrite` is `false` (default); and succeed in overwriting when the
      destination exists and `overwrite: true` is passed.
- [x] 6.3 Run `dotnet test` for `Vara.Core.Tests`, `Vara.Application.Tests`, and
      `Vara.Infrastructure.Tests`, and verify all tests pass, including the new cases from 6.1
      and 6.2.
