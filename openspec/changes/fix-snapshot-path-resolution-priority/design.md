## Context
`SnapshotPathResolver.TryResolve` currently tries, unconditionally in this order: (1) literal
match against `hasHistory`, (2) cwd-relative resolved path, stripped of the mirror root if it
falls inside the mirror, (3) cwd-relative resolved path, converted as an absolute source path via
`AbsolutePathMirrorMapper`. The existing spec text mandates this exact order, so today's behavior
is spec-conformant, not a stray implementation bug - fixing the review's reported scenario
requires changing the spec itself, which is why this is a `snapshot-history` capability change
rather than a plain bug fix.

`IsWithinMirror` (the private helper deciding whether the cwd-relative absolute path falls inside
the mirror) is being changed in the `harden-restore-destination-safety` change to take an injected
`isWithinMirror` predicate instead of its own lexical check; this change is independent of that one
and only reorders when the existing rules are tried, not how "is this inside the mirror" itself is
computed.

## Goals / Non-Goals

**Goals:**
- When the current working directory resolves inside the profile's mirror, try the cwd-relative
  interpretation before the literal one, so a file at the user's own location is not shadowed by
  an unrelated literal match elsewhere in the profile's recorded history.
- Preserve every existing interpretation - none are removed, only reordered - and preserve
  today's behavior exactly when the working directory is not inside the mirror.

**Non-Goals:**
- Reordering the cwd-relative-as-absolute-source-path interpretation (today's rule 3) ahead of
  the literal one. Unlike the mirror case, there is no cheap, meaningful precondition (analogous
  to "cwd is inside the mirror") gating rule 3 - `AbsolutePathMirrorMapper` will convert any
  absolute path regardless of whether it actually belongs to a configured source. Promoting rule 3
  ahead of literal unconditionally would change resolution for every invocation from every working
  directory on the filesystem, not just the narrow, well-motivated "standing inside the mirror"
  case the review raised, and carries materially more regression risk for the dominant
  run-from-anywhere, type-the-literal-path invocation pattern.
- Changing how "inside the mirror" is determined (that is `harden-restore-destination-safety`'s
  concern, if pursued) - this change reorders rules around whatever that check currently reports.
- Any change to `DirectoryArgumentResolver` beyond what naturally follows from
  `SnapshotPathResolver.TryResolve`'s behavior change, since it already delegates to `TryResolve`
  directly.

## Decisions
- **Gate the reordering on whether the cwd-relative absolute path resolves inside the mirror -
  the same condition rule 2 already computes - rather than introducing a new check.** When it
  does, try order becomes [rule 2, rule 1, rule 3]; when it does not, order remains [rule 1, rule
  3] (rule 2 never matches in this branch anyway, since its own precondition failed).
- **Keep `TryResolve`'s existing signature and single-pass, first-match-wins structure.** No new
  parameters are needed - this is purely a reordering of which candidate is checked against
  `hasHistory` first, using data (`absolute`, `IsWithinMirror`) the method already computes today.
- **Do not special-case "cwd equals the mirror root exactly"** differently from "cwd is some
  subdirectory of the mirror" - both already fall under the same `IsWithinMirror` check and are
  treated identically, consistent with today's rule 2.

## Risks / Trade-offs
- [Risk] A user working inside the mirror who genuinely intends the literal, unrelated path (an
  unusual but conceivable case - for example, deliberately typing another file's exact recorded
  mirror-relative path while standing somewhere else inside the mirror) now needs that literal
  path to not also coincidentally exist relative to their own location, or it resolves to the
  cwd-relative file instead.
  -> Mitigation: this exact ambiguity is the bug being fixed; the review's report and the common
  case (wanting "the file right here") both point the same direction. A user who needs the other,
  literal file can still reach it - typing an absolute path (source or mirror-relative) always
  works regardless of resolution order (rule 2/3 and the literal rule can produce the same result
  for an unambiguous absolute input).
- [Risk] Behavior for `history`/`restore`/`show`/`diff` now depends on the caller's current working
  directory in a way it did not for users who happened to always invoke these commands from
  inside the mirror and relied on literal-first matching.
  -> Mitigation: this dependency already existed for cwd-relative *non*-literal-matching inputs
  (rules 2/3 always resolved relative to cwd); this change only affects the case where a literal
  match *also* coincidentally exists elsewhere, which is inherently rare.
