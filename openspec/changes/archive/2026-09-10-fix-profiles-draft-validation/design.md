## Context

See proposal.md - Why for motivation. Relevant existing shape:

- `Vara.Application.Profiles.ProfileDraft` holds the mutable draft and exposes
  `Revalidate(IReadOnlyList<Profile>)`, `CurrentValidProfile` (the last successfully constructed
  `Profile`) and `CurrentError`. On a failed revalidation it deliberately *keeps* the previous
  `CurrentValidProfile` so the screen has something to show.
- `Vara.Cli.Commands.ProfilesCommand`'s edit screen calls `Revalidate` after each field edit, and
  its Save action currently branches on `draft.CurrentError is not null` and then persists
  `draft.CurrentValidProfile!`. The sources sub-screen (`RunSourcesScreen`) appends a new
  `SourceDraft` to `draft.Sources` before opening the per-source editor, and neither that append
  nor the sub-screen's return calls `Revalidate`.
- `SourceDraft.TryBuild` passes its own `List<string>` instances straight into `Source`'s
  constructor, which stores them by reference (`Excludes = excludes ?? []`).
- `ProfilesCommand.PromptOptionalInt` validates with `int.TryParse(value, out _)` and then parses
  with `int.Parse(input)`, both without an explicit culture, while `YamlProfileConfigWriter`
  writes with `CultureInfo.InvariantCulture` and `YamlProfileConfigLoader` reads with a
  culture-less `int.TryParse`.

## Goals / Non-Goals

**Goals:**
- Make "the screen says valid" and "Save writes this" both derive from a single, current
  validation of the draft, so no code path can persist a state the user is not looking at.
- Close the specific hole where a draft mutation (adding a source) bypasses revalidation, and make
  that hole structurally hard to reopen.
- Remove the shared-mutable-state aliasing between a `SourceDraft` and a constructed `Source`.

**Non-Goals:**
- Changing any validation *rule*. The domain constructors remain the single source of truth for
  what is valid; only *when* validation runs and *which* object is persisted change.
- Changing the save/discard model, the menu structure, or the writer.
- Error-handling for a failed *write* - that is `harden-profiles-save-resilience`'s scope.

## Decisions

### Save re-validates rather than trusting a cached flag
`ApplySave` currently consumes `draft.CurrentValidProfile`, and the Save action gates on
`draft.CurrentError`. Both are *cached results of the last `Revalidate` call*, and the caller is
responsible for having made that call recently enough. That contract is the root cause: it is
satisfied by every field-edit path and violated by one navigation path, and nothing in the type
system says so.

Instead, the Save action itself calls `Revalidate(profiles)` and branches on its return value,
persisting the `CurrentValidProfile` that this call just produced. This makes Save correct
independently of what any other screen did or forgot to do, and it makes the class of bug
non-reproducible rather than fixed in one spot.

Alternative considered: keep the cached-flag contract and add the missing `Revalidate` calls in
`RunSourcesScreen` (after the append, and before returning). Rejected as the *only* fix - it
repairs today's instance while leaving the same trap for the next screen added to the editor. The
missing calls are still added (so the displayed banner is correct while the user is *in* the
sources screen, per the live-validation requirement), but Save no longer depends on them.

### `CurrentValidProfile` stays "last known valid", but stops being Save's input
Keeping the last valid snapshot is still useful for display continuity, and the spec's "keep the
last valid snapshot on failure" behavior is covered by an existing test. Rather than change that
semantic, this design changes who may consume it: `ApplySave` takes the profile to persist as an
explicit argument (the one returned by the Save action's own successful `Revalidate`), instead of
reaching into `draft.CurrentValidProfile` itself.

Alternative considered: make `Revalidate` clear `CurrentValidProfile` on failure, so a stale
snapshot cannot be read at all. Rejected because it removes the deliberate "keep showing the last
good state" behavior the current spec and tests describe, for no additional safety once Save no
longer reads it.

### Adding a source revalidates immediately, and the incomplete source stays in the draft
When "Add source" appends a `SourceDraft`, the draft is revalidated right away, so an empty source
immediately produces the domain's own "path must be..." error rather than a silently-ignored row.
The alternative - only appending the source once its path is set, so an abandoned add leaves no
trace - was rejected because it contradicts the requirement that a source is part of the draft
from the moment it is added and re-validated as such, and because it would make the sources list
lie about what the user just did. Removing an unwanted source is already a supported, discoverable
action.

### Defensive copies at `Source` construction, not a frozen draft
`SourceDraft.TryBuild` copies its three string lists (`[.. Excludes]`) when constructing the
`Source`. This is the narrowest place to break the aliasing: `Source` is documented as immutable,
so the copy restores an invariant the domain already claims, and the draft stays freely mutable
for the editor. Making `SourceDraft` expose read-only lists, or deep-cloning the whole draft per
validation, were both rejected as larger changes for the same guarantee.

### Duplicate-name message is authored for the editor
The live uniqueness failure currently reuses `DuplicateProfileNameException`'s message ("...is
defined more than once in the configuration file"), which describes the loader's situation, not
the editor's - at that moment the name is defined once in the file and once in an unsaved draft.
The editor produces its own message naming the conflicting saved profile. The loader keeps
throwing `DuplicateProfileNameException` unchanged, so no existing loader test or error contract
moves.

Alternative considered: broaden the exception's message to cover both situations. Rejected because
it would degrade the loader's message (which correctly points at the file) to satisfy the editor.

### Invariant-culture numeric parsing in the editor
`PromptOptionalInt`'s validate-then-parse pair both take `CultureInfo.InvariantCulture`, matching
the writer's `ToInvariantString`. Values in the configuration file are machine-readable, not
localized, so parsing user entry with the same rules keeps entry and persistence symmetric. This
is a small consistency fix rather than a behavior change on any common locale.

## Risks / Trade-offs

- [Revalidating inside Save could surface an error the user has not seen before, making Save feel
  like it "suddenly" failed] → This is the intended behavior and is exactly the case the current
  bug hides; the error shown is the draft's real, current error, displayed on the same screen the
  user stays on.
- [Revalidating on add means the sources screen shows an error banner immediately after "Add
  source", before the user has had a chance to type anything] → Accepted: the requirement is that
  the banner describes the draft's current state, and an empty source genuinely is invalid. The
  message is the domain's own path-required text, which reads as guidance.
- [Copying source lists on every validation allocates per keystroke-completed edit] → Profile
  lists are a handful of entries; the design already accepts reconstructing the whole object graph
  per edit for the same reason.

## Migration Plan

No data or file-format migration: `profiles.yml`'s schema, the writer, and the loader are all
unchanged. The change is behavioral within a single interactive command, so it ships as an
ordinary release with no rollout steps and rolls back by reverting the commit.

## Open Questions

None.
