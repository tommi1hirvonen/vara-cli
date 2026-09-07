## 1. Real-path resolution helper

- [ ] 1.1 Add a `GetFinalPathNameByHandleW` `LibraryImport` to `Vara.Infrastructure.Interop.Kernel32`,
      alongside the existing `CreateHardLinkW` import, and verify it compiles and can resolve a
      known junction's real target in a manual smoke test
- [ ] 1.2 Implement a helper (e.g. in `Kernel32` or a small new internal type in
      `Vara.Infrastructure`) that walks up to the deepest existing ancestor of a path, opens it
      with `FILE_FLAG_BACKUP_SEMANTICS`, resolves its real path via `GetFinalPathNameByHandle`, and
      re-appends any non-existent trailing segments; falls back to `Path.GetFullPath` if no
      ancestor can be opened
- [ ] 1.3 Add unit tests: a plain path with no reparse points resolves unchanged; a path through a
      real junction (created via `mklink /J` in test setup) resolves to the junction's target; a
      not-yet-existing destination under an existing real directory resolves correctly with its
      trailing segment preserved literally; verify all pass

## 2. Reparse-aware mirror containment

- [ ] 2.1 Update `FileSystemContentStore.IsPathWithinRoot` to resolve both `root` and `candidate`
      via the new real-path helper before the existing prefix comparison
- [ ] 2.2 Add a `FileSystemContentStore` test: create a junction outside the mirror root pointing
      inside it, call `IsWithinMirror` on a path through that junction, and verify it now returns
      `true` (previously `false`)
- [ ] 2.3 Add a regression test confirming `IsWithinMirror`/`ResolveMirrorPath` behavior for
      ordinary (non-reparse) paths is unchanged, and verify all existing containment tests still
      pass

## 3. Atomic `ExtractTo`

- [ ] 3.1 Change `ExtractTo` to stage the copy under `_tempRoot` (reusing `CopyWithProgress`) and
      replace the destination via `File.Move(stagingPath, destinationAbsolutePath, overwrite:
      true)`, removing the current `File.Delete` call
- [ ] 3.2 Add a test that simulates a failed copy (for example, a `CopyWithProgress` that throws
      partway through, or a destination path becoming invalid mid-copy) and verifies the original
      destination file's content is unchanged
- [ ] 3.3 Add a regression test confirming a normal successful restore over an existing
      destination still ends up with exactly the new content, and verify all pass

## 4. Share the containment check with `SnapshotPathResolver`

- [ ] 4.1 Change `SnapshotPathResolver.TryResolve`'s signature to accept an
      `isWithinMirror: Func<string, bool>` parameter, remove its private `IsWithinMirror` method,
      and use the parameter in its place; when the parameter reports containment but the literal
      mirror-root prefix cannot be stripped, fall through to the absolute-source-path
      interpretation instead of producing an incorrect relative path
- [ ] 4.2 Update every call site (`ShowCommand`, `HistoryCommand`, `DiffCommand`, `RestoreCommand`,
      `DirectoryArgumentResolver`) to pass `services.ContentStore.IsWithinMirror`, and verify each
      command still builds and its existing tests pass
- [ ] 4.3 Add a `SnapshotPathResolver` unit test covering the reparse-point-via-injected-predicate
      case (a fake `isWithinMirror` that returns `true` for a path not sharing the mirror root's
      literal prefix) and verify it falls through to the source-path interpretation rather than
      resolving incorrectly

## 5. Verification

- [ ] 5.1 Run the full test suite (`dotnet test`) and verify all tests pass
- [ ] 5.2 Manually create a junction pointing into a test profile's mirror and confirm
      `vara restore` to a destination through that junction is refused with the existing
      "restoring into the live mirror is not permitted" error
- [ ] 5.3 Manually interrupt a restore over an existing large file (for example, by killing the
      process mid-copy) and confirm the original destination file's content is intact afterward
