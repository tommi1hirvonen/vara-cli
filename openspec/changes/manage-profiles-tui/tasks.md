## 1. Configuration writer (Vara.Core / Vara.Infrastructure)

- [ ] 1.1 Add `IProfileConfigWriter` to `Vara.Core.Abstractions` with a method to write an
      `IReadOnlyList<Profile>` to a configuration file path, and verify it compiles alongside the
      existing `IProfileConfigLoader`
- [ ] 1.2 Implement `YamlProfileConfigWriter` in `Vara.Infrastructure.Configuration` that builds a
      YamlDotNet DOM (`YamlMappingNode`/`YamlSequenceNode`/`YamlScalarNode`) from the profile list
      into a `YamlDocument`/`YamlStream` and serializes it via `YamlStream.Save(TextWriter, ...)`
      (no hand-driven `Emitter`/parsing events, and no reflection-based serializer, consistent
      with `YamlProfileConfigLoader`), and verify a unit test round-trips a profile list (write,
      then load it back with `YamlProfileConfigLoader`, and confirm the loaded profiles equal the
      originals field-for-field, including sources, retention, and concurrency)
- [ ] 1.3 Implement the write as atomic and first-write-safe: ensure the target's parent directory
      exists (`Directory.CreateDirectory`), write the document to a temp file in that same
      directory, then use `File.Replace` to swap it into place when the target path already
      exists, or `File.Move` when it does not (`File.Replace` throws `FileNotFoundException` on a
      missing destination, so it cannot be used unconditionally) - and verify unit tests cover
      both branches: writing to a path whose parent directory does not exist yet (first-ever run,
      no `~/.vara/`), and overwriting a path that already has a `profiles.yml`; also verify a test
      that simulates a failure after the temp file is written but before the swap confirms the
      original file (or its absence, for a first write) is left untouched
- [ ] 1.4 Verify a unit test confirms writing an empty profile list produces a file the loader
      reads back as zero profiles (matching the loader's own "empty configuration" scenario)
- [ ] 1.5 Verify a unit test confirms special YAML characters in a name/path/exclude entry (for
      example, a colon or a `#`) round-trip correctly through write-then-load

## 2. Cross-profile duplicate-name check (Vara.Application)

- [ ] 2.1 Add a small stateless helper (e.g. `ProfileNameUniqueness`) in
      `Vara.Application.Profiles` that, given the current profile list, a candidate name, and an
      optional name of the profile being replaced (for edits), reports whether the candidate name
      conflicts with a different profile, and verify unit tests cover: no conflict on an empty
      list, conflict against a different profile (case-insensitive), no conflict when the
      candidate name equals the name of the profile being replaced (rename-to-self), and conflict
      when the candidate name equals a different profile's name even if it also equals the
      replaced profile's old name coincidentally
- [ ] 2.2 Have `YamlProfileConfigLoader`'s existing duplicate-name check delegate to this same
      helper (or otherwise share the comparison logic) and verify existing loader tests
      (`DuplicateProfileNameException` scenarios) still pass unchanged

## 3. Profile draft model (Vara.Application)

- [ ] 3.1 Add a `SourceDraft` type holding a source's raw editable fields (path, recursive,
      exclude/include-glob/exclude-glob lists as free-form string lists) and a method to attempt
      constructing a `Source` from its current state, surfacing the domain constructor's
      exception message on failure, and verify unit tests cover a valid draft constructing
      successfully and an invalid one (relative path) surfacing the expected error text
- [ ] 3.2 Add a `ProfileDraft` type holding a profile's raw editable fields (name, target,
      `IList<SourceDraft>`, retention toggle + four counts, concurrency toggle + two counts) plus
      an `OriginalName` (`string?`, set once when the draft is initialized from an existing
      `Profile`, `null` for a new profile, and never updated by later edits to `Name`) that
      anchors which saved profile this draft replaces on Save and which entry the uniqueness check
      (Task 2) excludes from "conflicts with a different profile" - independent of the draft's
      current, possibly-renamed `Name`; a method to attempt constructing a `Profile` (and its
      `Source`s) from the current draft state using the domain constructors plus the Task 2
      uniqueness check (passing `OriginalName`, not the current `Name`, as the excluded name)
      against the rest of the profile list; and a way to initialize a draft from an existing
      `Profile` (for edit) or empty (for add); verify unit tests cover: an empty draft failing
      validation with a "name required"/"at least one source" style message, a fully valid draft
      constructing successfully, initializing a draft from an existing `Profile` reproducing the
      same values and capturing its name as `OriginalName`, and renaming a draft's `Name` while
      `OriginalName` stays fixed to the profile it started as
- [ ] 3.3 Verify unit tests cover live re-validation semantics from the spec: changing a source's
      path after the target root is already set re-triggers an overlap error when applicable;
      setting the target root after a source is already present re-triggers the same check;
      setting a draft's name to another saved profile's name produces the uniqueness error;
      renaming to the profile's own current name does not
- [ ] 3.4 Add a small result/error type (or reuse `ArgumentException`'s message) that the CLI
      presentation layer can display verbatim as the live validation error banner, and verify it
      captures enough detail (the message from `ProfileValidationException`-equivalent domain
      exceptions plus the new uniqueness check) to satisfy the spec's "identifying the problem"
      scenarios

## 4. `vara profiles` command and menu (Vara.Cli)

- [ ] 4.1 Register `YamlProfileConfigWriter` as `IProfileConfigWriter` in `Program.cs`'s DI
      container alongside the existing `IProfileConfigLoader` registration, add
      `ProfilesCommand.Create(...)` in `Vara.Cli.Commands` following the existing
      `*Command.Create` pattern (profile config loader/writer injected, no required arguments,
      plus a `--config` option matching other profile-scoped commands such as `ShowCommand` to
      target an alternate configuration file), register the command in `Program.cs` alongside
      the other commands, and verify `vara profiles --help` shows the new command and its
      `--config` option
- [ ] 4.2 Detect a non-interactive session at command entry by checking both directions -
      `Console.IsInputRedirected` (no real keyboard) as well as output redirection (reusing/
      extending `OutputMode`'s output-side check, which today only covers the progress-bar
      use case) - and report a clear error without opening any prompt if either is redirected;
      verify a test (or manual check) running the command with redirected input, redirected
      output, and both redirected all exit with an error and make no file change
- [ ] 4.3 Implement the main menu using `Spectre.Console.SelectionPrompt<T>` over a small CLI-local
      row type wrapping either an existing profile (name, target, source count) or the "Add new
      profile"/"Quit" sentinels, **with an explicit `Converter`/`UseConverter(...)` set for that
      row type** (its default `TypeConverter`-based conversion throws `InvalidOperationException`
      for any non-primitive, non-`[TypeConverter]`-annotated type - see design.md's "Command/
      presentation shape" decision), loading the profile list via `IProfileConfigLoader` (treating
      a missing file as zero profiles for this command specifically), and verify a manual run
      shows the expected rows for a sample `profiles.yml` without throwing
- [ ] 4.4 Wire "Quit" to exit without further action, and wire selecting an existing profile or
      "Add new profile" to open the edit screen (Task 5) with a draft seeded from that profile or
      empty, respectively

## 5. Profile edit screen (Vara.Cli)

- [ ] 5.1 Implement the edit screen's field-selection loop (name, target, sources, retention,
      concurrency, Save, Discard) displaying the draft's current values (including "not set" for
      unset optional/required-but-empty fields) and the current validation error banner (if any),
      re-running the `ProfileDraft` validation (Task 3) after every field change
- [ ] 5.2 Implement name and target editing via `Spectre.Console.TextPrompt<string>`, updating the
      draft and re-validating immediately
- [ ] 5.3 Implement the retention section as an optional toggle (present/absent) plus four integer
      prompts (`keep_daily`/`keep_weekly`/`keep_monthly`/`keep_yearly`) when present, and the
      concurrency section similarly for `scan_concurrency`/`transfer_concurrency`, re-validating
      after each change
- [ ] 5.4 Implement Save: attempt the full `ProfileDraft` validation; on success, use the draft's
      `OriginalName` (not its current `Name`) to find and replace the matching entry in the
      in-memory list - or append the draft as a new entry when `OriginalName` is `null` - then
      call `IProfileConfigWriter` to persist the updated list, and return to the main menu; on
      failure, redisplay the current error and remain on the edit screen; verify a manual run
      confirms both paths (successful save updates the file; save-while-invalid leaves the file
      untouched and keeps the screen open), plus a test/manual check that renaming an existing
      profile and saving replaces its old entry rather than adding a duplicate
- [ ] 5.5 Implement Discard: return to the main menu without calling the writer, verified by a
      manual run confirming the file is unchanged whether discarding a new profile or an edited
      existing one

## 6. Source sub-editor (Vara.Cli)

- [ ] 6.1 Implement the sources sub-list screen (list current `SourceDraft`s with a short summary
      per row, plus "Add source"/"Back") using `SelectionPrompt<T>` over a CLI-local row type,
      **with an explicit `Converter`/`UseConverter(...)` set** for the same reason as the main
      menu (Task 4.3), and verify a manual run shows the expected rows without throwing
- [ ] 6.2 Implement add/edit-source field editing (path via `TextPrompt<string>`, recursive via
      `ConfirmationPrompt`, exclude/include-glob/exclude-glob lists via repeated free-form entry),
      updating the containing `ProfileDraft`'s source list and re-validating the whole draft
      (Task 3) after each change, so target/source overlap errors surface immediately
- [ ] 6.3 Implement source removal (only permitted display-side; the draft's own validation
      already rejects a save with zero sources) and verify a manual run confirms the removed
      source no longer appears and, if it was the only source, the edit screen's validation error
      reflects that zero sources remain

## 7. Deletion flow (Vara.Cli)

- [ ] 7.1 Add a delete action to the main menu's per-row interaction (e.g. a hotkey on the
      selected row) that opens a `ConfirmationPrompt` naming the profile and target root
- [ ] 7.2 On confirmation, remove the profile from the in-memory list, call
      `IProfileConfigWriter` to persist the remaining list, and return to the refreshed main menu;
      on decline, return to the main menu with no change; verify a manual run confirms both paths
      and that no file/directory under the deleted profile's target root is touched

## 8. Cross-cutting verification

- [ ] 8.1 Add/extend `Vara.Cli.Tests` coverage (or manual test script, matching the project's
      existing testing conventions for interactive commands) exercising the end-to-end flow: add a
      profile with one source through to a saved file, edit it (rename, add a second source,
      configure retention), and delete it - confirming the final `profiles.yml` state after each
      step via `YamlProfileConfigLoader`
- [ ] 8.2 Run the full test suite (`dotnet test`) and confirm no existing profile-config,
      backup, restore, or prune tests regress
- [ ] 8.3 Build `Vara.Cli` with `dotnet build Vara.slnx -c Debug --nologo -v quiet` (or the
      project's standard build command) and confirm it succeeds with `PublishAot=true` still set,
      i.e. no reflection-based YAML serialization or other AOT-incompatible API was introduced
