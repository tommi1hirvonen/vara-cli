## Context
`Profile`'s constructor already validates target-vs-source overlap via `PathsOverlap`/
`NormalizePath` (see `Vara.Core.Configuration.Profile`, added by the `validate-profile-path-overlap`
change). It loops every source once, comparing it only to `targetRoot`. There is no loop
comparing sources to each other. `Source.Path` is already guaranteed absolute and non-empty by
`Source`'s own constructor, so this validation only needs to reason about already-normalized,
already-absolute strings.

## Goals / Non-Goals

**Goals:**
- Reject a profile whose sources overlap each other, at the same construction-time validation
  point as the existing target-vs-source check, using the same comparison semantics.
- Produce an error message that identifies both offending source paths, mirroring the existing
  target-vs-source error's clarity.

**Non-Goals:**
- Changing target-vs-source overlap behavior (already correct).
- Adding any runtime/scan-time deduplication safety net in `BackupExecutor`/`DirectoryFileSystemScanner`
  for overlapping sources - rejecting the configuration at load time is the chosen single point of
  enforcement, so no profile that would produce colliding mirror paths can ever reach the scanner.

## Decisions

- **Reuse the existing `PathsOverlap`/`NormalizePath` helpers.** They already implement the exact
  comparison semantics (case-insensitive, trailing-separator-safe, ancestor/descendant check) this
  new rule needs. Call `PathsOverlap(sources[i].Path, sources[j].Path)` for every unordered pair
  instead of introducing a second implementation.
- **Validate all pairs, not just adjacent ones.** With a typical profile having a handful of
  sources, an O(n^2) pairwise comparison is negligible; no need for sorting or interval-tree
  tricks.
- **Fail on the first overlapping pair found**, reporting both paths, rather than collecting every
  overlapping pair - consistent with how the existing target-vs-source check reports the first
  offending source it finds.
- **Validate in the same constructor pass as target-vs-source**, gated by the same
  `validateSourceOverlap` flag - the synthetic placeholder profile built by
  `ProfileResolver.TryResolveFromWorkingDirectory` has exactly one source, so a pairwise check
  over it is automatically a no-op; no separate opt-out is needed.

## Risks / Trade-offs
- [Risk] A profile that has relied on today's silent double-processing of an overlapping pair
  (unlikely, given the race/duplicate-row bugs it causes) would now fail to load.
  -> Mitigation: this is the intended behavior change; the error message names both offending
  paths so the user can fix their configuration in one pass, consistent with how the existing
  target-vs-source overlap error already guides users to fix their config.
