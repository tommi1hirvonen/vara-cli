## Why

`Profile`/`Source` validate that a target root and source paths are fully-qualified
(`Path.IsPathFullyQualified`) but store the string exactly as configured. A config value written
with forward slashes (e.g. `C:/Users/me/Docs`) or a mix of separators survives unchanged into the
scanner, which appends walked child segments with the native `\` separator, producing a
mirror-relative path with mixed `/`/`\` characters. That mixed-separator string is recorded
verbatim as the snapshot manifest's lookup key. `history`/`show`/`diff`/`restore`, which resolve a
user-typed path via `SnapshotPathResolver` (`Path.GetFullPath`, always `\`-separated), then produce
a manifest key that lexically differs from the one stored in the mirror-relative path
(`file_versions.relative_path`'s equality check is case-insensitive but not separator-insensitive),
so they report "No history exists" for a file that `browse` and backup handle correctly. The fix is
to canonicalize both paths once, at construction, so every downstream consumer already sees a
consistent absolute form.

## What Changes

- `Source`'s constructor (`src/Vara.Core/Configuration/Profile.cs`) replaces its verbatim `Path =
  path` assignment with `Path = System.IO.Path.GetFullPath(path)`, applied after the existing
  `IsPathFullyQualified` check so a relative path is still rejected outright rather than silently
  resolved against the process's working directory.
- `Profile`'s constructor applies the same treatment to `TargetRoot` (`Path.GetFullPath(targetRoot)`
  after its own `IsPathFullyQualified` check), closing the identical gap for the target root.
- No other production code changes: existing call sites that already run their own
  `Path.GetFullPath`/lexical normalization (`SnapshotPathResolver`, `FileSystemContentStore`,
  `Profile.PathsOverlap`) become redundant-but-harmless on an already-canonical input; this change
  does not remove that defensive normalization from them.
- No 8.3 short-path or reparse-point resolution is added - `Path.GetFullPath` normalizes
  separators, `.`/`..` segments, and trailing separators only, matching the existing limitation of
  `SnapshotPathResolver`'s own `Path.GetFullPath` call. This is a deliberate scope boundary, not an
  oversight.
- No config migration or upgrade handling is included - the project has no existing installs or
  stored manifests to reconcile.

## Capabilities

### Modified Capabilities

- `profile-config`: the "Source and target paths must be absolute" requirement is strengthened -
  an accepted target root or source path is now stored in canonical (`Path.GetFullPath`-normalized)
  form, not merely validated as fully-qualified.

## Impact

- **Affected code**: `src/Vara.Core/Configuration/Profile.cs` (`Source` and `Profile` constructors).
- **Affected tests**: `tests/Vara.Core.Tests/Configuration/ProfileTests.cs` (`SourceTests`,
  `ProfileTests`) gain coverage for forward-slash and mixed-separator input being normalized to the
  native separator.
- **Downstream consumers unaffected in behavior**: `DirectoryFileSystemScanner`,
  `AbsolutePathMirrorMapper`, `SnapshotPathResolver`, `FileSystemContentStore`, and
  `YamlProfileConfigLoader` require no changes - they already consume `Source.Path`/
  `Profile.TargetRoot` as opaque absolute-path strings.
