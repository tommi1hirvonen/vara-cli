## Context

See proposal.md - Why. `Source` and `Profile` (`src/Vara.Core/Configuration/Profile.cs`) each
validate their path with `Path.IsPathFullyQualified` but assign the constructor argument to the
property unchanged. `Source`/`Profile` are constructed from exactly two call sites:
`YamlProfileConfigLoader.ParseSource`/`ParseProfile` (Vara.Infrastructure, the real config-loading
path) and `ProfileResolver.TryResolveFromWorkingDirectory` (Vara.Application, a synthetic
placeholder profile whose `directory` argument is already `Path.GetFullPath`-resolved). Several
downstream consumers already call `Path.GetFullPath` themselves on a `Source.Path`/
`Profile.TargetRoot` value at the point of use (`SnapshotPathResolver.TryStripMirrorRootPrefix`,
`FileSystemContentStore`'s mirror-containment check), which is what surfaces the asymmetry: the
value that gets *recorded* in the manifest (via the scanner, which never re-normalizes) differs
from the value later used to *look it up*.

## Goals / Non-Goals

**Goals:**
- Make `Source.Path` and `Profile.TargetRoot` canonical (`Path.GetFullPath`-normalized) immediately
  after construction, so every downstream consumer - including ones not yet written - sees an
  already-consistent absolute path without needing to know to normalize it itself.
- Preserve the existing rejection of relative paths exactly as today (same exception type, same
  `ParamName`, same message wording pattern) - only the accepted-path storage behavior changes.

**Non-Goals:**
- Resolving 8.3 short path names or reparse points (junctions/symlinks) in the source/target path
  itself. `Path.GetFullPath` does not do this; closing that gap would require the existing
  `Kernel32.ResolveRealPath` helper (Vara.Infrastructure) at the config-loading boundary, which
  would need Vara.Core to either gain an Infrastructure dependency or have the loader canonicalize
  before construction. Out of scope for this change - the option A fix (canonicalize in the Core
  constructors only) was explicitly chosen over that layered alternative.
- Any data migration or reconciliation of previously-recorded manifests. The project has no
  existing installs to migrate.
- Removing the redundant `Path.GetFullPath` calls already present in `SnapshotPathResolver` and
  `FileSystemContentStore` - they become no-ops on an already-canonical input, but leaving them in
  place keeps those types correct in isolation (e.g. under direct unit tests that construct a
  `Source`/`Profile` by hand) and avoids a cross-cutting refactor unrelated to this fix.

## Decisions

**Canonicalize in the `Source`/`Profile` constructors themselves (Vara.Core), after the existing
`IsPathFullyQualified` check, rather than at the config-loading boundary (Vara.Infrastructure) or
in a separate normalization pass.**

- Guarantees the invariant "a constructed `Source`/`Profile` always holds a canonical path" no
  matter which of the two call sites constructs it, and no matter what future call site (or test)
  does - the type itself is self-defending, not dependent on every caller remembering to
  pre-normalize.
- Ordering matters: `IsPathFullyQualified` must run on the *raw* input first. `Path.GetFullPath`
  will happily turn a relative path like `Documents` into an absolute one by resolving it against
  the process's current working directory - if canonicalization ran first, the existing
  relative-path rejection tests (`Constructing_with_a_relative_path_throws`) would stop failing
  and a config authoring mistake would be silently accepted instead.
- `Path.GetFullPath(string)` (the single-argument overload, not the `(path, basePath)` overload) is
  used - the input is already known-fully-qualified at this point, so there is no meaningful "base
  path" to resolve against; this also matches `SnapshotPathResolver`'s own use of the two-argument
  overload only for its cwd-relative *raw user input*, which is a different scenario.

**Alternatives considered:**
- Canonicalizing only in `YamlProfileConfigLoader` (the real config-loading path), leaving
  `Source`/`Profile` themselves lexical. Rejected: `ProfileResolver`'s placeholder profile and any
  direct `new Source(...)`/`new Profile(...)` construction (tests, future callers) would remain
  unprotected, silently reintroducing the same class of bug for any input that isn't already
  canonical.
- Using `Kernel32.ResolveRealPath` for deeper canonicalization (8.3 short names, reparse points).
  Rejected for this change: it requires opening a real file handle, which changes failure modes
  (an inaccessible or momentarily-missing ancestor no longer fails purely on string shape) and
  would pull `Vara.Core` into a dependency on `Vara.Infrastructure` if done in the constructor - a
  larger, separate concern from the reported bug, and explicitly excluded by the chosen option.

## Risks / Trade-offs

- **[Risk]** `Path.GetFullPath` can throw `ArgumentException` for a small set of malformed inputs
  (for example, embedded invalid characters) that `IsPathFullyQualified` alone does not reject.
  → **Mitigation**: this exception is the same type already thrown by the existing
  `ArgumentException.ThrowIfNullOrWhiteSpace`/`IsPathFullyQualified` checks in the same
  constructors, and is already caught and re-wrapped as a `ProfileValidationException` by
  `YamlProfileConfigLoader`'s existing `catch (ArgumentException ex) when (...)` blocks - no new
  error-handling path is needed.
- **[Risk]** 8.3 short names remain unresolved, so a config authored with one will still produce a
  manifest key that differs from a later long-form lookup (or vice versa).
  → **Mitigation**: this is an explicit non-goal (see above); it is also a pre-existing limitation
  of `SnapshotPathResolver`'s own `Path.GetFullPath` call, so this change introduces no new
  inconsistency - it only closes the separator/relative-segment variant of the bug.
- **[Risk]** `Path.GetFullPath` normalizes forward slashes, doubled separators, and `.`/`..`
  segments, but does **not** strip a single trailing directory separator (confirmed:
  `GetFullPath("C:/Data/")` → `"C:\Data\"`, while `GetFullPath("C:\Data")` → `"C:\Data"` - these
  remain different strings). A config authored with an inconsistent trailing separator across
  otherwise-identical source paths is therefore not fully normalized by this change.
  → **Mitigation**: out of scope for Option A (this is not the bug reported, which was specifically
  about mixed `/`/`\` separators). `Profile.PathsOverlap`'s own `NormalizePath` already trims a
  trailing separator for overlap-comparison purposes, independently of this change, so overlap
  validation is unaffected. Closing this residual gap would require trimming logic beyond a plain
  `Path.GetFullPath` call, plus special-casing a drive root (`C:\` must not become `C:`, which is
  drive-relative, not absolute) - deliberately left out of this minimal fix.
