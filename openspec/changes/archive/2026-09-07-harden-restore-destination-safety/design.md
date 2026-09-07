## Context
Two independent gaps live in the same restore code path:

- `FileSystemContentStore.ExtractTo` (`Vara.Infrastructure`) does `File.Delete(destinationAbsolutePath)`
  then copies the blob directly to that path. `PlaceAtMirrorPath`, a few methods above it in the
  same file, already solves the equivalent problem for mirror writes: it copies/hardlinks into a
  fresh name under `_tempRoot`, then does a single `File.Move(stagingPath, mirrorPath, overwrite:
  true)` - an atomic rename that either fully lands or fully fails, never leaving a half-written
  destination.
- `FileSystemContentStore`'s private `IsPathWithinRoot(root, candidate)` - the implementation
  behind both `IsWithinMirror` (called by `SnapshotHistoryService.GuardDestination` before every
  restore) and `ResolveMirrorPath` (the mirror-write chokepoint) - normalizes both sides with
  `Path.GetFullPath` and does a case-insensitive prefix check. `Path.GetFullPath` is purely
  lexical: it collapses `..`/`.` segments and resolves relative-to-absolute, but it does not query
  the filesystem, so it never notices that some ancestor segment of `candidate` is a symlink or
  junction pointing somewhere else entirely. `SnapshotPathResolver` (`Vara.Application`) has its
  own `IsWithinMirror`, documented as "duplicated here in small form rather than shared, since
  `Vara.Application` does not depend on `Vara.Infrastructure`" - the same lexical-only limitation,
  independently implemented.
- Project references are strictly layered: `Vara.Application` -> `Vara.Core` and
  `Vara.Infrastructure` -> `Vara.Core`; `Vara.Application` does not reference
  `Vara.Infrastructure`. `Vara.Core.Abstractions.IContentStore` (implemented by
  `FileSystemContentStore`) is the interface both layers already share; `Vara.Cli` composes a
  concrete `IContentStore` and hands it to both `Vara.Application` services and command code
  (see `ProfileServiceFactory`/`ProfileServices`).
- `Vara.Infrastructure.Interop.Kernel32` already P/Invokes `CreateHardLinkW` via the
  source-generated `LibraryImport` pattern, specifically because the needed Win32 API has no
  first-class .NET wrapper on this target framework - the same will be true for real-path
  resolution.

## Goals / Non-Goals

**Goals:**
- `ExtractTo` never destroys existing destination content before the replacement content is fully
  and successfully staged.
- The mirror-containment check used to guard restores resolves reparse points before comparing,
  so a junction/symlink whose real target lies inside the mirror is correctly refused.
- Exactly one implementation of "is this path inside the mirror" exists, so the fix cannot
  silently drift out of sync between `FileSystemContentStore` and `SnapshotPathResolver` the way
  the two independent copies already have.

**Non-Goals:**
- Changing `ResolveMirrorPath` (the mirror-*write* containment chokepoint used during backup).
  It builds a destination path itself, from a mirror-relative path plus the mirror root, rather
  than validating an arbitrary externally supplied absolute path - the reparse-point escape this
  proposal closes is specific to a caller-supplied restore *destination*, which `ResolveMirrorPath`
  never receives. `IsPathWithinRoot`'s shared reparse resolution helps both call sites uniformly,
  but no separate behavior change is being made to `ResolveMirrorPath`/backup-execution's mirror
  write guard.
- Fully resolving reparse points when computing the *mirror-relative remainder* string in
  `SnapshotPathResolver` (interpretation rule 2 of "Flexible path input"). That remains a plain
  lexical prefix-strip against the mirror root. Only the yes/no "is this candidate inside the
  mirror, so should I try that interpretation" decision is upgraded to be reparse-aware; see
  Decisions below for what happens when those two disagree.
- Introducing an `IRealPathResolver`-style abstraction into `Vara.Core`. `Vara.Core` currently has
  no P/Invoke or OS-specific code; adding one just for this would spread the platform-specific
  concern across an extra layer for no benefit, when the simpler option (below) fits the existing
  composition pattern.

## Decisions

### Atomic `ExtractTo`
Change `ExtractTo` to mirror `PlaceAtMirrorPath`'s existing pattern exactly: copy the blob to a
fresh name under `_tempRoot` (via the same `CopyWithProgress` helper already used), then
`File.Move(stagingPath, destinationAbsolutePath, overwrite: true)` once the copy has fully
succeeded, removing the current `File.Delete(destinationAbsolutePath)` call entirely.
`File.Move(overwrite: true)` performs an atomic rename on the same volume; the existing
`GuardDestination` check already runs first and is unaffected. No alternative was seriously
considered - this is the same pattern the codebase already established and already trusts for the
mirror-write side.

### Reparse-aware containment: resolve the real path via `GetFinalPathNameByHandle`
Add a `GetFinalPathNameByHandleW` P/Invoke to `Vara.Infrastructure.Interop.Kernel32`, alongside the
existing `CreateHardLinkW` one, and a small helper that:
1. Walks up from `candidate` to the deepest ancestor that currently exists (a not-yet-existing
   restore destination is the common case - you cannot open a handle to a path that doesn't
   exist yet).
2. Opens that ancestor with `FILE_FLAG_BACKUP_SEMANTICS` (required to open a directory) and calls
   `GetFinalPathNameByHandle` to obtain its real, reparse-resolved path.
3. Re-appends the non-existent trailing segments (if any) literally to that real path.
4. Falls back to the plain `Path.GetFullPath(candidate)` result if no ancestor can be opened at
   all (for example, permission denied on every ancestor up to a drive root) - a fail-safe that
   preserves at least today's level of protection rather than throwing out of a routine
   containment check.

`root` (the mirror root) is resolved the same way once per check; it always exists.
Alternatives considered:
- **`FileSystemInfo.ResolveLinkTarget(returnFinalTarget: true)`** (.NET's built-in API) - rejected
  because it only resolves when the path *itself* is a symlink; it returns `null` for an ordinary
  file or directory sitting past a reparse point higher up its ancestor chain (exactly the
  "junction pointing into the mirror" scenario this proposal exists to close), so it does not
  cover the case that matters.
- **Refusing any path containing a reparse point anywhere in its ancestry**, without resolving
  where it actually points - rejected as unnecessarily broad: it would also refuse a perfectly
  safe restore destination that merely happens to sit under an unrelated junction, when the goal
  is specifically to catch the case where the real destination lands inside the mirror.

### Eliminate `SnapshotPathResolver`'s duplicate via a parameter, not a new shared project
`SnapshotPathResolver.TryResolve` gains an `isWithinMirror: Func<string, bool>` parameter (inserted
alongside the existing `hasHistory` delegate) and stops computing its own containment check.
Callers already hold an `IContentStore` at the point they call `TryResolve`
(`ShowCommand`/`HistoryCommand`/`DiffCommand`/`RestoreCommand`/`DirectoryArgumentResolver`, all in
`Vara.Cli`, all already resolve `services.ContentStore` from `ProfileServiceFactory`), so each
passes `services.ContentStore.IsWithinMirror` - no new project reference is needed anywhere, and
`Vara.Application` still never references `Vara.Infrastructure`. This was chosen over adding a
`Vara.Core`-level abstraction (see Non-Goals) because the composition root (`Vara.Cli`) already
wires exactly this kind of cross-layer collaboration via constructor/parameter injection for other
delegates (`hasHistory` itself is the existing precedent), so no new architectural seam is needed.

When the shared `isWithinMirror` predicate says "yes" but the literal mirror-root prefix cannot be
stripped from `absolute` (the reparse-point case: `absolute` doesn't start with the mirror root
string, but really does resolve inside it), `SnapshotPathResolver` treats that the same as "not
resolved by this rule" and falls through to interpretation rule 3 (absolute source path
conversion) per the Non-Goals note above, rather than fabricating an incorrect mirror-relative
path from a prefix strip that doesn't apply.

## Risks / Trade-offs
- [Risk] `GetFinalPathNameByHandle` requires opening a handle to the deepest existing ancestor,
  which is an extra filesystem call on every restore-destination check.
  -> Mitigation: this check already only runs once per restore destination (not per byte or per
  chunk), and restores are not a hot, high-frequency path the way backup transfers are.
- [Risk] The `Func<string, bool>` parameter changes `SnapshotPathResolver.TryResolve`'s public
  signature, touching five call sites.
  -> Mitigation: mechanical, same-shape change at each site (`services.ContentStore.IsWithinMirror`
  is already in scope wherever `TryResolve` is currently called); covered by existing tests for
  each command plus new ones for the delegate itself.
- [Risk] The deepest-existing-ancestor fallback means a restore destination where *every* ancestor
  up to a drive root is inaccessible (unusual, but possible under restrictive ACLs) falls back to
  the old lexical-only check for that one call.
  -> Mitigation: no regression versus today's behavior in that edge case; the reparse-aware
  improvement simply doesn't apply there, exactly like `GuardDestination`'s own existing failure
  modes for inaccessible destinations.
