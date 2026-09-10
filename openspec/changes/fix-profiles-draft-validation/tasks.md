## 1. Draft-level correctness (Vara.Application)

- [ ] 1.1 Make `SourceDraft.TryBuild` pass *copies* of its `Excludes`/`IncludeGlobs`/`ExcludeGlobs`
      lists into the `Source` constructor rather than the draft's own list instances, and verify a
      new unit test in `tests/Vara.Application.Tests/Profiles/SourceDraftTests.cs` that builds a
      `Source`, then adds an entry to the originating draft's exclude list, and asserts the
      already-built `Source`'s `Excludes` is unchanged
- [ ] 1.2 Replace the live duplicate-name error text in `ProfileDraft.Revalidate` (currently
      `new DuplicateProfileNameException(...).Message`, which says "is defined more than once in
      the configuration file") with a message authored for the editor that names the conflicting
      saved profile, leaving `YamlProfileConfigLoader` still throwing
      `DuplicateProfileNameException` unchanged; verify by updating
      `ProfileDraftTests.Setting_a_drafts_name_to_another_saved_profiles_name_produces_the_uniqueness_error`
      to assert the new text and confirming the loader's existing `DuplicateProfileNameException`
      tests still pass untouched
- [ ] 1.3 Verify a unit test covers the spec scenario "Later source edits do not change an
      already-validated profile": revalidate a valid draft, capture `CurrentValidProfile`, mutate
      a source draft's exclude list, and assert the captured profile still reports the original
      list contents

## 2. Save persists the current draft (Vara.Cli)

- [ ] 2.1 Change `ProfilesCommand.ApplySave` to take the profile to persist as an explicit
      parameter instead of reading `draft.CurrentValidProfile`, keeping its existing
      add-vs-replace-by-`OriginalName` logic, and verify the existing
      `ApplySave_adds_a_new_profile_when_OriginalName_is_null` and
      `ApplySave_replaces_the_entry_found_by_OriginalName_not_the_drafts_current_name` tests still
      pass after being updated to the new signature
- [ ] 2.2 Change the edit screen's `EditAction.Save` branch to call `draft.Revalidate(profiles)`
      itself and branch on its return value - persisting the profile that call produced on
      success, and redisplaying `draft.CurrentError` while staying on the edit screen on failure -
      instead of branching on the cached `draft.CurrentError`; verify by a test that mutates a
      draft into an invalid state *after* a successful revalidation and asserts the save path
      writes nothing (see task 4.1)
- [ ] 2.3 Verify no remaining code path outside `ProfileDraft` reads `CurrentValidProfile` to
      decide what to persist (grep for `CurrentValidProfile` in `src/Vara.Cli`) - the property
      stays for display continuity only

## 3. Sources sub-list revalidates on every mutation (Vara.Cli)

- [ ] 3.1 In `ProfilesCommand.RunSourcesScreen`, call `draft.Revalidate(profiles)` immediately
      after appending the new `SourceDraft` for the "Add source" action, so an incomplete source
      is reflected in the validation banner right away, and verify the sources screen's rendered
      error reflects a path-less source
- [ ] 3.2 In `ProfilesCommand.RunEditScreen`, call `draft.Revalidate(profiles)` after
      `RunSourcesScreen` returns, so the edit screen's banner is current on re-entry, and verify
      the banner shows the incomplete-source error rather than "Draft is valid."
- [ ] 3.3 Confirm the removal path (`SourceFieldAction.Remove`) still revalidates and that
      removing the only source surfaces the domain's "at least one source" error, per the existing
      spec scenario

## 4. Locale-independent numeric entry (Vara.Cli)

- [ ] 4.1 Change `ProfilesCommand.PromptOptionalInt`'s `int.TryParse` validation and `int.Parse`
      conversion to both use `CultureInfo.InvariantCulture`, matching
      `YamlProfileConfigWriter.ToInvariantString`, and verify by inspection that entry and
      persistence now use the same parsing rules

## 5. Regression coverage

- [ ] 5.1 Add a `Vara.Cli.Tests` test reproducing the reported bug at the level the automated test
      host can drive: start from a valid draft that has been revalidated, append an empty
      `SourceDraft` (as "Add source" does), then exercise the save path and assert it refuses -
      reporting the incomplete-source error and leaving the configuration file untouched - rather
      than writing the earlier valid snapshot
- [ ] 5.2 Extend `ProfilesEndToEndTests` with a step that adds a second source, saves, and
      confirms via `YamlProfileConfigLoader` that the written file contains *both* sources, so a
      dropped-source regression fails the suite
- [ ] 5.3 Run `dotnet test` and confirm the whole suite passes, with no regression in the existing
      profile-config, profile-draft, writer, backup, restore, or prune tests
- [ ] 5.4 Build with `dotnet build Vara.slnx -c Debug --nologo -v quiet` and confirm it succeeds
      with `PublishAot=true` still set on `Vara.Cli`
