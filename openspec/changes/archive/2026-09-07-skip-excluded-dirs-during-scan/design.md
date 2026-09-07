## Context
`DirectoryFileSystemScanner.Walk` recurses through every plain (non-reparse-point) directory
unconditionally, yielding file paths and reparse-point paths as it goes. `ScanSource` then computes
`relativePath` for each yielded path and calls `IsExcluded`/`MatchesGlob` to decide whether to
`yield return` an entry for it - filtering happens entirely after traversal already paid its I/O
cost. `IsExcluded` compares a relative path against `source.Excludes` (plain literal/prefix
strings, normalized to `/`-separated form) - it doesn't need a `Matcher` and can be evaluated
against a directory's own relative path just as easily as a file's.

## Goals / Non-Goals

**Goals:**
- Skip enumerating a directory's contents entirely when that directory's own relative path
  matches the source's literal exclude list.
- Ensure no `ScanFailure` is recorded for anything beneath a directory that was skipped this way.
- Preserve today's behavior for every other case: files, non-excluded directories, and glob-based
  include/exclude filtering of files.

**Non-Goals:**
- Directory-level pruning based on `IncludeGlobs`/`ExcludeGlobs`. Glob patterns in this codebase
  are evaluated per full relative path via `Matcher.Match`, which is designed for filtering
  individual candidate paths, not for deciding whether a directory is worth entering. Determining
  whether an arbitrary exclude glob (for example `**/*.tmp`) could ever prune a whole directory in
  advance, without changing matching semantics for edge cases the `Matcher` already handles
  correctly, is a larger effort (likely involving `Matcher.Execute` against a
  `DirectoryInfoWrapper` and reconciling it with this scanner's own reparse-point handling) and is
  left for a future change if glob-heavy profiles show it's worth it.
- Any change to reparse-point (symlink/junction) detection or reporting - a reparse point is still
  reported once and never descended into, regardless of this change.

## Decisions
- **Push the existing `IsExcluded` check into `Walk`, evaluated against each subdirectory's own
  relative path before recursing into it**, rather than duplicating a second exclusion check or
  rewriting traversal around the `Matcher` library. `Walk` already computes
  `Path.GetRelativePath(root, directory)` for failure reporting, so the relative path needed for
  the exclusion check is already available at the point of recursion.
- **Only the literal `Excludes` list gates traversal; `IncludeGlobs`/`ExcludeGlobs` remain
  per-entry, post-traversal filters**, per the Non-Goals above. This keeps the change small and
  focused on the concrete, common case the review raised (a configured directory name like
  `node_modules` or `.git`), without speculatively redesigning glob handling.
- **A file source (`Source.Path` pointing directly at a single file) is unaffected** - the early
  `File.Exists(source.Path)` branch in `ScanSource` never calls `Walk` and has nothing to prune.

## Risks / Trade-offs
- [Risk] A directory whose own relative path does not match the exclude list, but that should
  arguably have been pruned by a broader pattern (for example a user expecting `bin` to be
  excluded everywhere but only configuring `Excludes: ["bin"]`, which today already only matches
  a top-level or exact-nested `bin` per `IsExcluded`'s existing semantics) sees no behavior change
  from this proposal - `IsExcluded`'s matching rules themselves are unchanged, only *when* they're
  evaluated for directories.
  -> Mitigation: none needed; this preserves current match semantics exactly, only moving the
  check earlier for directories.
- [Risk] Skipping traversal also means a permission failure on the excluded directory's own
  metadata (for example `File.GetAttributes` on the excluded directory node itself, called by the
  *parent* `Walk` invocation before recursing) is still possible and still reported, since that
  check happens on the directory entry itself before we know to skip it.
  -> Mitigation: this is consistent with existing behavior for any other entry (its own
  `GetAttributes` failure is always reported); only recursion *into* the directory is skipped.
