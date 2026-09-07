## 1. Containment exception type

- [x] 1.1 Add `MirrorPathEscapesTargetRootException : IOException` to
      `src/Vara.Core/Abstractions/IContentStore.cs` (or a new file alongside it), carrying the
      offending mirror-relative path and the resolved absolute path, with a message identifying
      both plus the target root; verify the project builds.

## 2. Shared containment-check helper

- [x] 2.1 In `src/Vara.Infrastructure/Storage/FileSystemContentStore.cs`, extract the
      resolve-and-prefix-check logic currently inline in `IsWithinMirror` into a private static
      helper (e.g. `IsPathWithinRoot(string root, string candidate)`), and have `IsWithinMirror`
      call it; verify existing `IsWithinMirror_*` tests in
      `tests/Vara.Infrastructure.Tests/Storage/FileSystemContentStoreTests.cs` still pass
      unchanged (pure refactor, no behavior change).
- [x] 2.2 Add a private `ResolveMirrorPath(string mirrorRelativePath)` helper that combines
      `_mirrorRoot` with the given relative path, resolves it via `Path.GetFullPath`, and either
      returns the resolved absolute path or throws
      `MirrorPathEscapesTargetRootException` (using the helper from 2.1) when the resolved path is
      not within `_mirrorRoot`; verify with a new unit test asserting the exception for a
      UNC-style relative path (e.g. `\\srv\share\file.txt`) and for a `..\..\`-style relative path,
      and asserting the resolved path is returned unchanged for a normal relative path.

## 3. Wire the guard into every mirror-write entry point

- [x] 3.1 Replace `Path.Combine(_mirrorRoot, mirrorRelativePath)` in `PlaceAtMirrorPath` with a
      call to `ResolveMirrorPath`, called before any file is created or staged; verify with a new
      test asserting `PlaceAtMirrorPath` throws `MirrorPathEscapesTargetRootException` for an
      escaping relative path and creates no file anywhere (assert the staging temp file and any
      would-be destination are both absent), and that existing `PlaceAtMirrorPath` tests for
      normal relative paths still pass unchanged.
- [x] 3.2 Replace both `Path.Combine(_mirrorRoot, fromRelativePath)` and
      `Path.Combine(_mirrorRoot, toRelativePath)` in `MoveMirrorEntry` with calls to
      `ResolveMirrorPath`, resolving and checking both endpoints before any `File.Exists`/
      `File.Move` call runs; verify with new tests asserting the exception is thrown (and no file
      is moved, created, or deleted) when the "from" path escapes, and separately when the "to"
      path escapes.
- [x] 3.3 Replace `Path.Combine(_mirrorRoot, mirrorRelativePath)` in `RemoveFromMirror` with a call
      to `ResolveMirrorPath`, called before the existing `File.Exists`/`File.Delete` logic; verify
      with a new test asserting the exception is thrown and no file is deleted anywhere for an
      escaping relative path.
- [x] 3.4 Add a regression test reproducing the original finding end-to-end at the
      `FileSystemContentStore` level: call `PlaceAtMirrorPath` with a UNC-shaped mirror-relative
      path pointing at a real temp file, and assert the exception is thrown, the temp file's
      content and attributes (including read-only) are completely unchanged, and no file was
      created under the configured mirror root.

## 4. Port documentation

- [x] 4.1 Update the XML doc comments on `IContentStore.PlaceAtMirrorPath`, `MoveMirrorEntry`, and
      `RemoveFromMirror` in `src/Vara.Core/Abstractions/IContentStore.cs` to state that each throws
      `MirrorPathEscapesTargetRootException` when the resolved mirror path would fall outside the
      target root; verify the project builds with no XML-doc warnings.

## 5. Executor and scanner-level coverage

- [x] 5.1 In `tests/Vara.Application.Tests/Backup/BackupExecutorTests.cs`, add a test (using
      `FakeContentStore`'s existing "throw this instead" seams from
      `tests/Vara.Application.Tests/Backup/Fakes.cs`) asserting that when `PlaceAtMirrorPath`,
      `MoveMirrorEntry`, or `RemoveFromMirror` throws `MirrorPathEscapesTargetRootException`, the
      run completes without an unhandled exception, records the affected path as failed, and
      continues processing all other operations - confirming the existing
      `IOException`-based catch in `BackupExecutor` requires no code change to handle the new
      exception type.
- [x] 5.2 In `tests/Vara.Infrastructure.Tests/FileSystem/DirectoryFileSystemScannerTests.cs`,
      add a test scanning a source whose path is UNC-shaped, asserting the scanner itself still
      completes and yields a `ScannedEntry` with the unmapped (UNC) mirror path (confirming
      `AbsolutePathMirrorMapper.ToMirrorPath`'s current pass-through behavior for UNC is
      unchanged - the new guard lives at the content-store write boundary, not in scanning).

## 6. Full verification

- [x] 6.1 Run `dotnet build Vara.slnx --nologo -v quiet` and confirm the solution builds with no
      warnings or errors.
- [x] 6.2 Run `dotnet test` for the full solution and confirm all tests pass, including every
      pre-existing test across `Vara.Core.Tests`, `Vara.Infrastructure.Tests`,
      `Vara.Application.Tests`, and `Vara.IntegrationTests`, to confirm no regression from the new
      containment guard.
