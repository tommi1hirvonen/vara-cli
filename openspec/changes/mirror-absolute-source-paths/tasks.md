## 1. Scanner path mapping

- [ ] 1.1 Add a small helper (e.g. `AbsolutePathMirrorMapper` or a static method) that converts a full absolute source path into its mirror-relative form by stripping the colon after a drive letter (e.g. `C:\Users\john\file.txt` -> `C\Users\john\file.txt`), and verify it with unit tests covering a root-level drive file, a deeply nested path, and a path with mixed separators.
- [ ] 1.2 In `DirectoryFileSystemScanner.ScanSource`, keep using `Path.GetRelativePath(root, path)` only for `IsExcluded`/`MatchesGlob` evaluation, and pass the new absolute-path-derived mapping (from 1.1) as the value used to construct each `ScannedEntry`'s `RelativePath`, for directory-source entries.
- [ ] 1.3 Update the single-file source branch to use the same absolute-path-derived mapping instead of `Path.GetFileName(source.Path)`, and verify a single-file source produces the same mirror path shape as a directory-source entry at that location.
- [ ] 1.4 Verify move/rename detection, exclude/glob filtering, and symlink/junction recording still function unchanged by re-running `Vara.Infrastructure.Tests` and confirming only the expected `RelativePath` assertions (task 2) need updates.

## 2. Update existing tests for the new path format

- [ ] 2.1 Update `DirectoryFileSystemScannerTests` assertions that expect source-relative `RelativePath` values (e.g. `"a.txt"`, `Path.Combine("sub", "b.txt")`) to expect the new absolute-path-derived values, and verify the test project builds and passes.
- [ ] 2.2 Update any `Vara.Application.Tests`, `Vara.Core.Tests`, and `Vara.IntegrationTests` fixtures or assertions that hardcode source-relative mirror paths (e.g. fake scanners, planner/differ tests, real-content-store integration tests) to use absolute-path-derived paths consistent with their configured `Source` paths, and verify each affected test project passes.

## 3. Verification

- [ ] 3.1 Run the full test suite (`dotnet test`) and confirm all tests pass with the new mirror path mapping.
- [ ] 3.2 Manually exercise a profile with two sources sharing a same-named top-level file or subfolder (per the new "Two sources share a subfolder or file name at the same depth" scenario) and confirm both entries land at distinct, non-colliding mirror paths after a backup run.
