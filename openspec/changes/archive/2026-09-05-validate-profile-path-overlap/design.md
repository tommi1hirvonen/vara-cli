## Context

`Profile` (src/Vara.Core/Configuration/Profile.cs) already validates structural invariants in its
constructor (non-empty name/target, at least one source) and throws `ArgumentException` for
violations - this runs regardless of how the `Profile` is constructed (YAML loader, or the
synthetic profile built by `ProfileResolver.TryResolveFromWorkingDirectory`). Separately,
`YamlProfileConfigLoader.ParseProfile` (src/Vara.Infrastructure/Configuration/YamlProfileConfigLoader.cs)
translates config-level problems into `ProfileValidationException` (a `ProfileConfigException`)
so the CLI can print a clean, user-facing message instead of a raw exception. See proposal.md -
Why for the motivating bug (self-mirroring backups) and Capabilities for the affected spec.

## Goals / Non-Goals

**Goals:**
- Detect and reject same-profile target/source path overlap (equal, ancestor, or descendant in
  either direction) before any scan or transfer runs.
- Keep the check dependency-free and pure string/path logic - no filesystem access needed to
  detect the overlap itself (existence of the paths is irrelevant to the check).
- Surface a clear, actionable error through the existing `ProfileConfigException` hierarchy when
  loaded from YAML.

**Non-Goals:**
- Cross-profile overlap detection (profile A's target vs. profile B's source) - explicitly
  deferred per proposal.md.
- Detecting overlap through symlinks, junctions, or other indirect aliasing of the same physical
  location (e.g. two different-looking paths that resolve to the same folder via a mount point) -
  this check is a textual containment check on the configured path strings, consistent with how
  `IsExcluded` and mirror-path comparisons already work elsewhere in the codebase.
- Changing `DirectoryFileSystemScanner` or any runtime backup/scan behavior - the fix is entirely
  at config-load/construction time.

## Decisions

**Where the check lives**: in the `Profile` constructor itself, not only in the YAML loader. This
matches the existing pattern where `Profile` enforces its own structural invariants
unconditionally (so `ProfileResolver.TryResolveFromWorkingDirectory`'s synthetic profile - which
uses the same directory as both target and its one source - stays valid, since target *equals*
its single source there by design, not by user error... see Risk below) and ensures the invariant
holds no matter who constructs a `Profile`. Throwing `ArgumentException` from the constructor
matches the existing style for the other constructor checks (name, target, sources).

**How overlap is detected**: normalize both paths with the same technique already used in
`DirectoryFileSystemScanner.IsExcluded` (trim trailing `\`/`/`, compare case-insensitively), then
check equality or whether one normalized path, followed by a separator, is a prefix of the other.
This avoids introducing a new normalization convention.

**Where the user-facing error is raised**: `YamlProfileConfigLoader.ParseProfile` catches the
`ArgumentException` thrown by the `Profile` constructor and rethrows it as a
`ProfileValidationException(name, reason)`, exactly like every other structural validation already
performed in `ParseProfile` (missing target, empty sources, etc.). This keeps `Profile`'s
constructor as the single source of truth for the invariant while preserving the existing
config-loading error contract (one bad profile -> clear, named error -> load aborts).

**Alternative considered**: performing the check only in `YamlProfileConfigLoader` and leaving the
`Profile` constructor untouched. Rejected because it would leave the invariant unenforced for
non-YAML-constructed profiles and split "profile structural validity" across two places instead of
one.

**Handling `ProfileResolver`'s synthetic placeholder profile**: `ProfileResolver.
TryResolveFromWorkingDirectory` builds a synthetic `Profile` whose single placeholder `Source` is
intentionally the same directory as the target root (documented as "never used for
browsing/restoring, only present because `Profile`'s constructor requires at least one source").
Enforcing the overlap check unconditionally would break this existing, intentional construction.
Resolution: give the `Profile` constructor an additional optional parameter (e.g.
`validateSourceOverlap = true`) that callers can set to `false` to skip only the new overlap check
while every other existing constructor validation (name, target, sources non-empty) still runs
unconditionally. `ProfileResolver.TryResolveFromWorkingDirectory` is the only caller that passes
`false`, with a comment referencing why. `YamlProfileConfigLoader` never passes it (uses the
default `true`), so every YAML-loaded profile is always checked.

**Alternatives considered for the placeholder conflict**:
- Moving the check entirely out of `Profile` into a validator called only by the YAML loader -
  rejected as it reopens the "split validation across two places" problem above.
- Changing the placeholder `Source`'s path to a non-overlapping dummy value - rejected as a less
  explicit fix: it relies on every future reader noticing the dummy path is deliberately "wrong"
  rather than seeing an explicit opt-out parameter at the call site.

## Risks / Trade-offs

- [Adding an opt-out parameter to `Profile`'s public constructor slightly widens its API surface
  for what is essentially one internal call site's escape hatch] -> Mitigated by defaulting the
  parameter to `true` (checked) and documenting on the parameter exactly why/where `false` is
  legitimate, so accidental misuse elsewhere is unlikely and easy to spot in review.
- [Textual containment check misses symlink/junction aliasing] -> Accepted as a non-goal; matches
  existing precedent (`IsExcluded`, mirror-path comparisons) of treating configured paths as plain
  strings rather than resolving them on disk.
- [False positive on unrelated paths that share a string prefix without a separator, e.g. target
  `C:\backup` and source `C:\backup2`] -> Mitigated by requiring the separator-boundary check
  (prefix match must be followed by a path separator), not a raw string prefix match.
