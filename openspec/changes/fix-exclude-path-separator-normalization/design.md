## Context

`DirectoryFileSystemScanner.IsExcluded` (see proposal.md - Why) compares a scanned entry's relative path against each configured `Excludes` entry using raw string comparisons (`Equals` / `StartsWith`), keyed off `Path.DirectorySeparatorChar`. `Path.GetRelativePath` always returns paths using the host platform's native separator (`\` on Windows). Exclude entries come from YAML as free-form strings and are only `Trim()`-ed and `TrimEnd('\\', '/')`-ed - no separator normalization is applied. `MatchesGlob`, used for `IncludeGlobs`/`ExcludeGlobs`, already normalizes by calling `relativePath.Replace('\\', '/')` before matching, so it does not have this problem.

## Goals / Non-Goals

**Goals:**
- Make `IsExcluded` match a nested exclude entry regardless of which separator (`/` or `\`) it or the scanned path uses.
- Keep the fix localized to `IsExcluded`; no change to `Source`/config parsing, the public `Excludes` type, or glob handling.

**Non-Goals:**
- Do not change glob matching behavior (`MatchesGlob` already normalizes correctly).
- Do not attempt to support platform-specific separator semantics beyond normalizing `/` and `\` - no handling of other separator characters.
- Do not add logging/warnings for excludes that fail to match; that is a separate concern from this fix.

## Decisions

**Normalize both sides to a single separator before comparing.** In `IsExcluded`, replace `\` with `/` (matching the convention `MatchesGlob` already uses) in both `relativePath` and each `normalized` exclude entry before the `Equals`/`StartsWith` checks, and compare using `/` as the separator instead of `Path.DirectorySeparatorChar`. This keeps the two matching code paths (plain excludes and glob excludes) consistent with each other, and it is a minimal, localized change confined to the one method responsible for the bug.

Alternative considered: normalize the exclude list once at `Source` construction time instead of per-comparison in `IsExcluded`. Rejected for this change - it would touch the `Source`/config-loading layer for no behavioral benefit, since `IsExcluded` already re-normalizes trailing separators per call; keeping the fix inside `IsExcluded` mirrors the existing pattern and minimizes the diff.

## Risks / Trade-offs

- [Risk] An exclude entry that intentionally contains a literal `\` or `/` as part of a filename (rare on the target filesystems) could be mis-normalized. → Mitigation: Windows and the filesystems this tool targets do not allow `/` or `\` in individual file/directory names, so normalization cannot misinterpret an intra-name character as a separator.
- [Risk] Existing exclude configurations that happen to rely on the current (broken) non-matching behavior would start matching after the fix, silently changing what gets backed up. → Mitigation: the only entries affected are ones that were already intended as excludes but silently failing to match: making them match is the intended fix, not a new behavior change requiring a migration path.
