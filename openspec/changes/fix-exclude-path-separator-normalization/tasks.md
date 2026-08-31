## 1. Fix separator normalization

- [ ] 1.1 Update `DirectoryFileSystemScanner.IsExcluded` to normalize both the scanned relative path and each configured exclude entry to a single separator (`/`) before comparing, and use that separator (instead of `Path.DirectorySeparatorChar`) in the `StartsWith` check; verify by inspecting the diff that both sides of every comparison are normalized identically.

## 2. Test coverage

- [ ] 2.1 Add a scanner test asserting that a nested exclude entry written with a forward slash (e.g. `"sub/excluded"`) skips files under that nested folder on Windows, mirroring the existing `Excluded_subfolder_is_skipped` test; verify by running `dotnet test tests/Vara.Infrastructure.Tests` and confirming the new test passes.
- [ ] 2.2 Add a scanner test asserting that a nested exclude entry written with the native backslash separator (e.g. `"sub\\excluded"`) still skips files under that nested folder, to guard the existing behavior alongside the fix; verify the same `dotnet test` run covers it.

## 3. Full verification

- [ ] 3.1 Run the full test suite (`dotnet test`) and confirm all tests pass, with no regressions in other scanner or glob-matching tests.
