## Context

See `proposal.md` for motivation. Two independent call sites share the same root cause:

- `DirectoryArgumentResolver.Resolve` (used by `browse`) calls `SnapshotPathResolver.TryResolve`
  with a `hasHistory` predicate (`DirectoryTracked`) that explicitly returns `true` for an empty
  prefix (i.e. the mirror root) regardless of whether the profile has any tracked content at all.
- `RestoreCommand.RunRecursiveRestore` (used by `restore --recursive`) calls the same
  `SnapshotPathResolver.TryResolve` with a different predicate (`IsDirectoryTracked`, built on
  `SnapshotHistoryService.ListDirectory`/`NoSuchDirectoryException`) that is *not* hard-coded, but
  in practice is equivalent for the root case: `ListDirectory`'s own `anyTracked` check considers
  the root "tracked" as soon as the profile has any file anywhere, which is true for every
  non-empty profile.

`SnapshotPathResolver.TryResolve` itself is shared by `history`/`restore`/`show`/`diff` for
single-file paths and must not change behavior for those callers - the `.`/blank
short-circuit problem is specific to directory arguments, where an empty/`.` input has a
trivially-always-valid literal interpretation that a real file or non-root directory path never
has.

## Goals / Non-Goals

**Goals:**
- Fix `.`/blank directory-argument resolution for `browse` and `restore --recursive` so that,
  when the current working directory is inside a profile source (not the mirror), it maps
  through the same absolute-source-path translation already used for every other path form,
  consistent with how single-file `restore`/`history`/`show`/`diff` already behave.
- When the source-mapped location has no tracked history, fall back to the mirror root and tell
  the user briefly why, so the fallback isn't mistaken for the requested directory.
- Keep the fix local to directory-argument resolution; do not change
  `SnapshotPathResolver.TryResolve`'s behavior or call signature for single-file callers.

**Non-Goals:**
- No change to resolving a non-root, non-blank directory argument (e.g. `browse subdir`) - this
  already falls through to the absolute-source-path mapping correctly.
- No change to `history`/`restore`/`show`/`diff`'s single-file resolution order or predicate
  semantics.
- No change to behavior when the current working directory *is* inside the profile's mirror -
  the cwd-relative mirror candidate (step 1 in that branch) already wins there, and root already
  resolves to root correctly in that case.

## Decisions

**Decision: Fix at the directory-resolution call sites, not inside `SnapshotPathResolver`.**
`SnapshotPathResolver.TryResolve` has no notion of "directory" vs. "file" - it only sees a
`hasHistory` predicate. The trivially-true-for-root behavior is a property of the *predicates*
passed in by `DirectoryArgumentResolver` and `RestoreCommand`, not of the shared resolver. Rather
than adding directory-specific branching to the shared resolver (which both file and directory
callers would need to reason about), each directory call site gets a small wrapper that:
1. Detects the `.`/blank case up front.
2. When the cwd does not resolve inside the mirror, tries the absolute-source-path candidate
   first (calling the *real* history predicate, not the trivially-true one) and uses it if it
   has tracked history.
3. Only if that fails, falls back to the literal mirror-root interpretation - and flags that this
   was a fallback, so the caller can print the explanatory message.
This keeps `SnapshotPathResolver.TryResolve` untouched for every existing caller (file paths, and
non-root directory paths, both unaffected) and confines the special case to where it actually
applies.

**Decision: Make the fallback observable via return signature, not by having the resolver print
output itself.** `DirectoryArgumentResolver`/`RestoreCommand`'s directory-resolution helper lives
in `Vara.Cli.Commands`, but printing is presentation-layer behavior owned by each command
(`BrowseCommand` via `AnsiConsole`, `RestoreCommand` via `OutcomeStyle`/`StandardError`). The
resolver returns whether it fell back to the root as an explicit `bool` (or a small result type)
alongside the resolved path, and each command decides how to phrase/print the message
consistently with its existing output style, rather than the resolver reaching into console
output directly.

**Alternative considered: swap the literal/absolute-source-path order for directories
generally (proposal's "Option B").** Rejected as broader than necessary: it would also change
resolution for a non-root directory argument name that *happens* to collide with a real
mirror-relative path when the cwd is outside the mirror (an edge case, but an unnecessary
behavior change beyond what was asked for). The targeted `.`/blank-only fix (this design's
"Option A") achieves the same practical outcome - `.` behaves like restore's single-file
resolution - without touching resolution for any input that already works today.

**Decision: One shared helper for both call sites.** `DirectoryArgumentResolver` and
`RestoreCommand.IsDirectoryTracked`/`RunRecursiveRestore` currently duplicate similar "is this
directory tracked" plumbing with slightly different predicates. Rather than patching each
independently (and risk the two drifting further), extract the `.`/blank-with-fallback-flag logic
into one place `DirectoryArgumentResolver` exposes, parameterized by the same kind of
`hasHistory`-shaped predicate each caller already has (`DirectoryTracked` for browse,
`IsDirectoryTracked` for restore), so both commands call the same tested logic and both print
their own copy of the fallback message using their own existing output conventions.

## Risks / Trade-offs

- [Risk] A profile whose mirror root path itself happens to be the user's cwd-mapped source
  location in a way that's ambiguous (e.g. cwd is exactly a configured source root with nothing
  yet backed up under it) could produce the fallback message on every browse/restore, which might
  read as noisy for that narrow case → Mitigation: the message only fires when the source-mapped
  location truly has zero recorded history, which is also exactly the case where showing the
  mirror root silently (today's behavior) is misleading; a one-line explanation is preferable to
  either silent wrong output or a hard error.
- [Risk] Existing test `DirectoryArgumentResolverTests.The_mirror_root_itself_always_resolves`
  encodes the old trivially-true behavior and will need to change to reflect the new fallback
  semantics (still resolves to root, but only after trying the source mapping, and now signaling
  a fallback) → Mitigation: update it alongside new tests added for the source-mapped-root and
  fallback-with-message paths, per tasks.md.
- [Trade-off] This introduces a small amount of duplicated "resolve `.`/blank with fallback
  signaling" surface between `browse` and `restore --recursive` call sites even after
  consolidating the core logic, since each command still owns its own message-printing - accepted
  as necessary given the two commands' differing output styles (table listing vs. success/error
  outcome lines).
