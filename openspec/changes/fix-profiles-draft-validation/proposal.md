## Why

The `vara profiles` editor can tell a user their draft is valid when it is not, and then save
something other than what the screen shows. The edit screen persists a *cached* last-known-valid
profile rather than the draft's current state, and one editing path - adding a source - mutates
the draft without re-validating it. Concretely: from a valid draft, choosing "Manage sources" →
"Add source" → "Back" → "Back" leaves an empty, path-less source in the draft while the screen
still reports "Draft is valid"; choosing "Save" then silently writes the pre-add profile and drops
the newly added source with no error. That breaks the capability's core promise that every field
change is validated live and that Save persists the draft the user is looking at.

## What Changes

- Every mutation of a profile draft - including adding a source and returning from the sources
  sub-list - re-validates the whole draft, so the displayed validity banner always describes the
  draft's current state rather than a stale earlier state.
- "Save" re-validates the draft at the moment it is chosen, and persists the profile produced by
  *that* validation. A draft that is invalid at that moment is refused with its current error and
  writes nothing; a valid draft is written exactly as displayed.
- A profile draft's newly added source is part of the draft immediately, so an incomplete source
  (for example, one with no path yet) makes the draft invalid and visibly blocks Save, instead of
  being silently discarded.
- A saved profile no longer shares mutable state with the draft it came from: a profile's source
  exclude/include-glob/exclude-glob lists are snapshotted at validation time, so later edits to
  the draft cannot retroactively alter an already-validated profile.
- The live duplicate-name error is phrased for the editor (naming the conflicting saved profile)
  rather than reusing the loader's "is defined more than once in the configuration file" wording,
  which describes a situation the user is not in.
- Numeric entry in the editor (concurrency values) is parsed with the same invariant, culture-independent
  rules used to write those values back to the configuration file, so a machine whose locale
  formats numbers differently cannot reject or misread a value the writer just produced.

## Capabilities

### New Capabilities
(none)

### Modified Capabilities
- `profile-management`: tightens "Draft changes are validated live, after every field edit" so
  that adding a source counts as a change requiring re-validation; tightens "Save persists only a
  fully valid draft" so Save validates the draft's current state and persists exactly that state;
  tightens "Sources are managed as an editable sub-list within a profile draft" so an added source
  belongs to the draft (and affects its validity) from the moment it is added.

## Impact

- `Vara.Cli.Commands.ProfilesCommand`: the edit screen's Save action, the sources sub-list screen's
  add path, and the return path from the sources sub-list; the editor's integer parsing helper.
- `Vara.Application.Profiles.ProfileDraft` / `SourceDraft`: the validation entry point used by Save,
  and defensive copying of a source draft's string lists when a `Source` is constructed.
- `Vara.Core.Configuration` domain types, the configuration loader, the configuration writer, and
  every other command are unaffected - no validation *rule* changes, only when validation runs and
  which object is persisted.
- Test impact: `Vara.Application.Tests` (draft revalidation/snapshot behavior) and
  `Vara.Cli.Tests` (save-path behavior), both of which already cover this area.
