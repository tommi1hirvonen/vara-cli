## 1. Canonicalize `Source.Path`

- [ ] 1.1 In `Source`'s constructor (`src/Vara.Core/Configuration/Profile.cs`), after the existing
  `IsPathFullyQualified(path)` check, assign `Path = System.IO.Path.GetFullPath(path)` instead of
  the raw `path` argument. Verify the existing `Constructing_with_a_relative_path_throws` test still
  fails on relative input (i.e. the check still runs on the raw input first).
- [ ] 1.2 Add a test in `tests/Vara.Core.Tests/Configuration/ProfileTests.cs` (`SourceTests`)
  asserting that a source path written with forward slashes (e.g. `C:/Users/me/Docs`) is stored
  with the native directory separator throughout (`source.Path` contains no `/`), and verify the
  test passes.
- [ ] 1.3 Add a test asserting that two source paths denoting the same location but differing only
  in separator style or a trailing separator (e.g. `C:/Data/` vs `C:\Data`) produce identical
  `source.Path` values, and verify the test passes.

## 2. Canonicalize `Profile.TargetRoot`

- [ ] 2.1 In `Profile`'s constructor (`src/Vara.Core/Configuration/Profile.cs`), after the existing
  `IsPathFullyQualified(targetRoot)` check, assign `TargetRoot = System.IO.Path.GetFullPath(targetRoot)`
  instead of the raw `targetRoot` argument. Verify the existing relative-target-root rejection test
  still fails on relative input.
- [ ] 2.2 Add a test in `tests/Vara.Core.Tests/Configuration/ProfileTests.cs` (`ProfileTests`)
  asserting that a target root written with forward slashes is stored with the native directory
  separator throughout (`profile.TargetRoot` contains no `/`), and verify the test passes.

## 3. Verify overlap validation still uses the canonical value correctly

- [ ] 3.1 Verify (or add if missing) a test constructing a `Profile` whose target root and a source
  path are written with different separator styles but denote the same or an overlapping location
  (e.g. target `C:/backup` and source `C:\backup\Documents`), asserting the existing overlap
  validation (`PathsOverlap`) still rejects it correctly now that both inputs are canonicalized
  before the check runs.

## 4. Full validation

- [ ] 4.1 Run `dotnet test tests/Vara.Core.Tests` and verify all tests pass, including the new ones
  from tasks 1-3.
- [ ] 4.2 Run `dotnet build Vara.slnx --nologo -v quiet` (or the project's standard whole-solution
  build command) and verify it succeeds with no new warnings introduced by this change.
