## 1. Scanner root-safe path trimming

- [x] 1.1 Replace `source.Path.TrimEnd('\\', '/')` in `DirectoryFileSystemScanner.ScanSource` (src/Vara.Infrastructure/FileSystem/DirectoryFileSystemScanner.cs) with `Path.TrimEndingDirectorySeparator(source.Path)`, and verify by inspection that no other call site in this file feeds a similarly-trimmed value directly into a filesystem API
- [x] 1.2 Add a `DirectoryFileSystemScannerTests` case (tests/Vara.Infrastructure.Tests/FileSystem/DirectoryFileSystemScannerTests.cs) that configures a source whose path is a bare drive root of a temporary test drive/volume (or, if a real extra drive/volume isn't available in CI, a directory-junction/subst-drive fixture that reproduces a root path), and verify the scan enumerates the root's actual contents rather than any other directory
- [x] 1.3 Run the `Vara.Infrastructure.Tests` suite and verify all tests pass, including the new drive-root case and existing scanner tests for non-root sources

## 2. Rooted-path validation on Source and Profile

- [x] 2.1 In `Source`'s constructor (src/Vara.Core/Configuration/Profile.cs), add a check using `Path.IsPathFullyQualified(path)` alongside the existing `ArgumentException.ThrowIfNullOrWhiteSpace(path)`, throwing an `ArgumentException` that names the offending value when the path is not fully qualified
- [x] 2.2 In `Profile`'s constructor, add the same `Path.IsPathFullyQualified` check for `targetRoot`, throwing an `ArgumentException` that names the offending value when it is not fully qualified
- [x] 2.3 Trace how `Source`/`Profile` construction failures are currently surfaced to the user during profile loading (e.g. wrapped with the profile name into a validation error), and confirm the new rootedness failures flow through that same path so the reported error identifies the profile and the offending field, per the profile-config spec delta
- [x] 2.4 Add `ProfileTests`/`Source`-construction test cases (tests/Vara.Core.Tests/Configuration/ProfileTests.cs) covering: a relative source path is rejected, a relative target root is rejected, and a fully-qualified target root with fully-qualified source paths is accepted, and verify they pass
- [x] 2.5 Add or update a profile-loading test (wherever the existing config-file parsing tests for validation errors live) covering a configuration file containing a relative source or target path, and verify it reports a validation error identifying the profile and field rather than throwing an unhandled exception or silently accepting the path

## 3. Regression sweep

- [x] 3.1 Search the existing test suites (`Vara.Core.Tests`, `Vara.Infrastructure.Tests`, and any profile-loading/config tests) for fixtures that configure a relative source or target path, and update them to use absolute paths so they continue to pass under the new validation
- [x] 3.2 Build the solution (`dotnet build Vara.slnx`) and run the full test suite, and verify there are no build errors and no regressions beyond the intentionally-updated fixtures from 3.1
