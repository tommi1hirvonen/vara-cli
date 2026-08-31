## Why

`DirectoryFileSystemScanner.IsExcluded` compares a scanned entry's relative path directly against each configured `exclude` string without normalizing path separators. On Windows, `Path.GetRelativePath` always produces backslash-separated paths, but a nested exclude written with forward slashes (e.g. `sub/folder/output`, a natural way to write a path in YAML) never matches, because neither the `Equals` nor the `StartsWith` check accounts for the separator mismatch. The failure is silent: no error or warning is logged, and the excluded files are simply included in the mirror. For a backup tool, a silently-ignored exclude can mean private or large data unexpectedly ends up in the backup target. The glob matcher (`ExcludeGlobs`) already guards against this by normalizing to forward slashes before matching; the plain `Excludes` list does not.

## What Changes

- Normalize path separators in both the exclude pattern and the relative path before comparing in `DirectoryFileSystemScanner.IsExcluded`, so an exclude written with either `/` or `\` matches consistently regardless of the runtime platform's separator.
- Add scanner test coverage for a nested exclude path written with the non-native separator (forward slash on Windows) to prevent regression.

## Capabilities

### New Capabilities
(none)

### Modified Capabilities
- `backup-execution`: clarify that the "Source exclusion rules honored" requirement's exclude-list matching is insensitive to which path separator (`/` or `\`) is used in a nested exclude entry.

## Impact

- `src/Vara.Infrastructure/FileSystem/DirectoryFileSystemScanner.cs` (`IsExcluded` method).
- `tests/Vara.Infrastructure.Tests/FileSystem/DirectoryFileSystemScannerTests.cs` (new test case).
- No configuration schema or public API changes; behavior-only fix that makes previously-silently-ignored excludes take effect.
