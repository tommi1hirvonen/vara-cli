## Context

See proposal.md - Why for motivation. Relevant existing shape:

- `Vara.Application.Profiles.ProfileNameUniqueness` is a static class with one pure method,
  `ConflictsWithAnotherProfile(IEnumerable<string> profileNames, string candidateName, string? excludedName)`,
  implementing a case-insensitive comparison that skips the first entry matching `excludedName`.
- Its two callers sit in different layers: `Vara.Infrastructure.Configuration.YamlProfileConfigLoader`
  (file-level duplicate detection) and `Vara.Application.Profiles.ProfileDraft` (live validation).
- Project references today: `Vara.Application` → `Vara.Core`; `Vara.Infrastructure` → `Vara.Core`
  *and* `Vara.Application` (the latter added solely for this helper); `Vara.Cli` → both.
- `Vara.Core.Configuration` already owns the rest of the profile invariants (`Profile`, `Source`,
  `RetentionPolicy`, `ConcurrencySettings`, and the `ProfileConfigExceptions` family, including
  `DuplicateProfileNameException`).

## Goals / Non-Goals

**Goals:**
- Restore `Vara.Infrastructure`'s dependency set to `Vara.Core` only.
- Keep the duplicate-name rule defined exactly once, still shared by the loader and the editor.

**Non-Goals:**
- Changing the comparison's semantics, signature, or messages - byte-for-byte the same decisions
  for every input.
- Moving any other type, or reorganizing the layers more broadly.
- Any change to the editor's or the loader's behavior. (The editor's duplicate-name *message* is
  changed by `fix-profiles-draft-validation`, not here.)

## Decisions

### `Vara.Core.Configuration` is the right home, not a new shared project
The rule is a configuration invariant about a set of profiles, in the same family as "a target
must not overlap its own sources" - it just happens to span profiles rather than live inside one,
which is why it could not be a `Profile` constructor check. `Vara.Core.Configuration` already
holds `DuplicateProfileNameException`, the exception this rule leads to, so co-locating the check
with its own exception is the least surprising placement. Both existing callers already reference
`Vara.Core`, so nothing needs a new reference.

Alternative considered: leave the type in `Vara.Application` and let `Vara.Infrastructure` keep
its reference. Rejected - a compile-time reference is not scoped to the one helper; it permanently
authorizes infrastructure code to depend on application types, which is the layering the project
otherwise maintains.

Alternative considered: introduce a small shared "kernel" project below both. Rejected as
over-engineering: `Vara.Core` already is that project.

### Keep it a static pure function with the same signature
The move is a relocation, not a redesign: same name, same parameters, same
`StringComparison.OrdinalIgnoreCase` behavior, same "skip only the first `excludedName` match"
rule (which the existing tests pin down precisely). Keeping the signature identical means the two
call sites change only their `using`, and the existing test file moves without edits to its
assertions - so the refactor is verifiable by the tests continuing to pass unchanged.

Alternative considered: fold the check into a new `ProfileList`/collection type in `Vara.Core` and
have the loader build through it. Rejected - a larger redesign that would change the loader's
control flow, for no benefit to the stated problem.

### Verify the layering, don't just fix it once
Removing the `ProjectReference` from `Vara.Infrastructure.csproj` is the actual enforcement: with
it gone, any future infrastructure code reaching for an application type fails to compile. No
additional architecture-test tooling is introduced - the project graph is the check.

## Risks / Trade-offs

- [A pure move can silently leave a stale duplicate behind if the original type is not deleted] →
  The old type is removed in the same change, and the removed project reference guarantees the
  loader cannot still be binding to it.
- [The test project layout may need a new/for-`Vara.Core` home for the uniqueness tests] → The
  tests move alongside the type into the test project that already covers `Vara.Core`; assertions
  are unchanged, so a behavioral regression would show up immediately.
- [Namespace change is source-breaking for any external consumer] → There are none; every
  reference is inside this repository.

## Migration Plan

Single atomic commit: move the type, update the two `using` directives, move the test file, drop
the project reference, build and run the suite. No runtime, configuration, or data migration.
Rollback is reverting the commit.

## Open Questions

None.
