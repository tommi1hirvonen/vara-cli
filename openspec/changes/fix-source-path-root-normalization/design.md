## Context

See `proposal.md` - Why for the motivation and confirmed repro. Two independent fixes are bundled
here because they close the same class of hole (a source/target path that silently means
something other than what the user configured):

1. `DirectoryFileSystemScanner.ScanSource` computes `root = source.Path.TrimEnd('\\', '/')` and
   passes `root` straight into `Walk`/`Directory.EnumerateFileSystemEntries`/`Path.GetRelativePath`.
   For a drive-root path like `C:\`, this yields `C:`, a Windows drive-relative path.
2. Neither `Profile` nor `Source` construction (`src/Vara.Core/Configuration/Profile.cs`) validates
   that `targetRoot` or a source's `Path` is absolute/rooted - only non-empty. A relative path is
   accepted today and would be resolved against whatever the process's working directory happens
   to be whenever a scan or backup later runs.

`Profile.NormalizePath` (used only by `PathsOverlap`, the source/target overlap check) has the
same `TrimEnd('\\', '/')` shape but is not affected by the drive-relative bug, because
`PathsOverlap` always re-appends a directory separator before comparing (`normalizedTarget +
Path.DirectorySeparatorChar`), so the bare `"C:"` form it can produce is never hit by a filesystem
API. It is left as-is.

## Goals / Non-Goals

**Goals:**
- Make a drive-root source path (e.g. `C:\`) scan the real drive root, with no silent fallback to
  some other directory.
- Reject, at profile-load time, any target root or source path that is not absolute/rooted, with a
  validation error that names the profile and the offending path.
- Keep the fix scoped to path normalization and validation - no change to scanning, exclude/glob
  matching, mirroring, or any other backup-execution behavior beyond the drive-root case.

**Non-Goals:**
- No change to `Profile.NormalizePath`/`PathsOverlap` - not affected by this bug (see Context).
- No support for UNC (`\\server\share\...`) paths beyond whatever `Path.IsPathFullyQualified`
  already accepts - out of scope, matching the existing UNC non-goal from the
  `mirror-absolute-source-paths` change.
- No attempt to auto-correct a relative path (e.g. by resolving it against the config file's
  location) - rejecting it is simpler, and a silently-resolved relative path is exactly the kind of
  ambiguity this change is closing off.
- No change to how excludes (`DirectoryFileSystemScanner.cs:192`) are normalized - excludes are
  always source-relative strings, never drive roots, so they are not exposed to this bug.

## Decisions

**Decision: Replace `source.Path.TrimEnd('\\', '/')` in `DirectoryFileSystemScanner.ScanSource`
with `Path.TrimEndingDirectorySeparator(source.Path)`.**
`Path.TrimEndingDirectorySeparator` is a BCL method purpose-built for this: it trims one trailing
separator beyond the root, and leaves an actual root untouched. Verified directly:
`TrimEndingDirectorySeparator("C:\")` → `"C:\"` (unchanged), `TrimEndingDirectorySeparator("C:\Users\john\")`
→ `"C:\Users\john"`. This is a one-line, drop-in replacement at the single call site
(`DirectoryFileSystemScanner.cs:65`) that actually feeds the trimmed value into filesystem APIs.
Alternative considered: `Path.GetFullPath(source.Path)` before trimming. Rejected - it also
canonicalizes `..` segments, casing, and slash style, which is a broader behavior change than this
bug needs and isn't required once paths are validated as already-rooted (see next decision).

**Decision: Validate rootedness with `Path.IsPathFullyQualified`, applied to `Source.Path` and
`Profile.targetRoot` in their constructors, alongside the existing non-empty checks.**
`Path.IsPathFullyQualified` (unlike `Path.IsPathRooted`) correctly rejects a drive-relative path
like `C:foo` as not fully qualified, which matters here since that's exactly the malformed shape at
the center of this bug - so validation and the fix both converge on treating `C:`-style paths as
unacceptable input rather than a valid one. On failure, the constructor throws
`ArgumentException` with a message naming the field and the offending value, consistent with the
existing `PathsOverlap` validation error's shape (profile name is added by the caller that already
wraps profile-level validation errors today).
Alternative considered: `Path.IsPathRooted`. Rejected - it returns `true` for `C:foo` (a
drive-relative path), which is precisely the malformed shape this change needs to reject, so it
would not close the gap.

**Decision: Validate in the `Source`/`Profile` constructors, not in a separate pre-check before
`DirectoryFileSystemScanner` runs.**
This mirrors the existing pattern (`ArgumentException.ThrowIfNullOrWhiteSpace`, `PathsOverlap`) of
failing fast at construction so every caller - not just the scan path - gets the same guarantee,
and so the scanner can continue to assume its input is already well-formed.

## Risks / Trade-offs

- **[Risk/Trade-off]** The new rootedness validation is a **BREAKING** change: any existing profile
  configuration with a relative source or target path (previously silently accepted) will now fail
  to load -> Mitigation: none needed beyond a clear, actionable validation error; this is a
  pre-release application (see the `mirror-absolute-source-paths` change's Migration Plan) with no
  compatibility guarantee to preserve, and a relative path was never a coherent configuration to
  begin with.
- **[Risk]** `Path.IsPathFullyQualified` has platform-specific semantics (e.g. a leading `/` alone
  is fully qualified on Unix but not fully qualified on Windows) -> Mitigation: none needed; this
  project targets Windows paths throughout (drive letters, `\` separators) per existing specs and
  code, so Windows semantics are the correct and only semantics in play here.

## Migration Plan

None required for the scanner fix - it only changes behavior for the previously-broken drive-root
case. The new rootedness validation has no data migration path (see Risks above): a profile
configured with a relative source or target path must be edited to use an absolute path before it
will load again.
