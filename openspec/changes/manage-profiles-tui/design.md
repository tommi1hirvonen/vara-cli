## Context

See proposal.md - Why/What Changes for motivation and scope. Relevant existing shape:

- `Vara.Core.Configuration` (`Profile`, `Source`, `RetentionPolicy`, `ConcurrencySettings`) are
  immutable records whose constructors enforce all structural invariants (required fields,
  absolute paths, target/source overlap, non-negative/positive numeric fields) and throw
  `ArgumentException`/`ArgumentOutOfRangeException` on violation.
- `Vara.Core.Abstractions.IProfileConfigLoader` / `Vara.Infrastructure.Configuration.
  YamlProfileConfigLoader` read `~/.vara/profiles.yml` into `Profile`s using YamlDotNet's
  low-level DOM API (`YamlStream`/`YamlMappingNode`/etc.), deliberately avoiding YamlDotNet's
  reflection-based (de)serializer because `Vara.Cli` builds with `PublishAot=true`. The loader
  also enforces file-level rules a single `Profile` cannot know about itself: duplicate profile
  names across the whole list, and file-level YAML/structure errors
  (`ProfileConfigMalformedException`, `DuplicateProfileNameException`, etc. in
  `ProfileConfigExceptions.cs`).
- `Vara.Cli` already depends on `Spectre.Console` (used today for tables/progress bars) and
  `System.CommandLine` for command wiring (`Program.cs`, one `*Command.Create(...)` static
  factory per command, registered on `RootCommand`).
- There is currently no writer - only the read path exists.

## Goals / Non-Goals

**Goals:**
- Add a writer that regenerates `profiles.yml` from an in-memory `IReadOnlyList<Profile>`, built
  the same AOT-safe way as the loader (hand-built YAML DOM/text, no reflection-based serializer).
- Add an editing/draft model that can represent a profile's fields before they form a valid
  `Profile`, re-validate on every change (reusing the domain constructors rather than
  re-implementing their rules), and expose Save/Discard.
- Add the `vara profiles` Spectre.Console-driven presentation, wired the same way every other
  command is wired.

**Non-Goals:**
- Flag-based `vara profiles add/edit/remove` subcommands (explicitly deferred in proposal.md).
- Preserving comments/formatting in `profiles.yml` across a save (explicitly out of scope per
  proposal.md).
- Any change to `Profile`/`Source`/`RetentionPolicy`/`ConcurrencySettings` themselves, or to how
  any other command loads/uses profiles.

## Decisions

### Draft model: a mutable builder per profile, not a mutable `Profile`
`Profile`/`Source` stay immutable records (unchanged, per Non-Goals). The edit screen instead
holds a small mutable draft type per aggregate - e.g. `ProfileDraft` (nullable/raw fields: name,
target, sources, retention toggle + counts, concurrency toggle + counts) and `SourceDraft` (path,
recursive, three raw string lists) - that can represent a state `Profile`'s constructor would
reject (missing name, zero sources, mid-edit numeric field). "Live validation" means: after every
field change, attempt to construct a `Profile` (and its `Source`s) from the current draft state;
on success, cache the constructed `Profile` as the current valid snapshot and clear the error; on
`ArgumentException`/`ArgumentOutOfRangeException`, keep the last valid snapshot (if any) but
display the exception's message as the current error. This means the domain layer's existing
validation rules are the single source of truth for "is this shape valid" - the draft layer never
duplicates a rule like "target must be absolute", it just decides *when* to ask `Profile`/`Source`
to check.

`ProfileDraft` also carries an `OriginalName` (`string?`, `null` for a brand-new profile, set to
the profile's saved name when the draft is initialized from an existing `Profile`) that is never
changed by editing the `Name` field afterward. This is the anchor Save and the uniqueness check
both need once renaming is allowed: since `Profile` is a value-equality record and the draft's
current `Name` may no longer match anything in the saved list, `OriginalName` is what identifies
*which* saved profile (if any) this draft replaces, and what the uniqueness check excludes from
"conflicts with a different profile" - not the draft's current, possibly-just-typed `Name`.
Without this anchor, renaming a profile cannot be told apart from colliding with itself, and Save
would have no reliable way to find the entry it's replacing in the saved list.

Alternative considered: give `Profile`/`Source` mutable setters or a `with`-based incremental
validity API, so the draft *is* a `Profile` throughout. Rejected because it would let an invalid
intermediate state escape the constructor's guard (defeating the point of validating construction
today), and because other code (backup/restore/prune pipelines) can currently assume any `Profile`
instance in hand is fully valid - adding a "partially valid Profile" mode would weaken that
invariant everywhere, not just in the editor.

### Cross-profile duplicate-name check: a small stateless validator, not part of `Profile`
`Profile`'s constructor validates only itself; only the loader currently knows about "siblings"
(`YamlProfileConfigLoader`'s `seenNames`). Introduce a small stateless helper (e.g. a
`ProfileNameUniqueness` check in `Vara.Application.Profiles`) that takes the full current profile
list, the draft's candidate name, and (for edits) the name of the profile being replaced so a
no-op rename doesn't conflict with itself. The loader is left as-is; both the loader and the new
draft/editing service call the same small check so the rule is defined once. This mirrors the
proposal's framing (`profile-config` unchanged; a shared validator reused by both).

Alternative considered: duplicate the uniqueness loop directly in the draft/editing service.
Rejected only because it is the same three-line rule the loader already has; a shared helper avoids
having two copies of "case-insensitive name comparison" drift apart later.

### Writer: hand-built YAML DOM/emission, mirroring the loader's AOT constraint
Add `IProfileConfigWriter`/`YamlProfileConfigWriter` (mirroring `IProfileConfigLoader`/
`YamlProfileConfigLoader`'s placement in `Vara.Core.Abstractions`/`Vara.Infrastructure.
Configuration`) that takes `IReadOnlyList<Profile>` and produces the full YAML document text by
building YamlDotNet's DOM node types (`YamlMappingNode`, `YamlSequenceNode`, `YamlScalarNode`) into
a `YamlDocument`/`YamlStream`, then calling `YamlStream.Save(TextWriter, ...)` to serialize it -
`YamlStream` already walks its node tree and drives the low-level event/emitter machinery
correctly, so there is no need to hand-drive `YamlDotNet.Core.Emitter` with raw parsing events
directly. No `[YamlMember]`/reflection-based serialization, consistent with why the loader avoids
it (`PublishAot=true` in `Vara.Cli.csproj`).

The write path needs to handle both "file already exists" and "first write ever" cases explicitly,
since they require different .NET file APIs:
1. Ensure the target's parent directory exists (`Directory.CreateDirectory`) - on a fresh machine
   `~/.vara/` itself may not exist yet.
2. Build the document and save it to a temp file in that same directory (same-volume, so the
   final move is atomic).
3. If the target path already exists, use `File.Replace` (or delete-then-move) to swap the temp
   file into place; if it does not exist yet, use `File.Move` instead - `File.Replace` throws
   `FileNotFoundException` when the destination doesn't already exist, so a writer that only ever
   calls `File.Replace` would fail on every user's very first save.

This two-path branch is what makes the write atomic in both the common case (editing an existing
`profiles.yml`) and the first-run case (no file, possibly no `~/.vara/` directory, yet), matching
the "Saving regenerates..." requirement's atomicity scenario.

Alternative considered: emit plain text directly (string-building `"profiles:\n  - name: ...\n"`)
without going through YamlDotNet's node/tree API at all. Rejected because correct YAML scalar
quoting/escaping (e.g. a path containing `:` or a name containing a `#`) is exactly what
`YamlStream.Save` already handles correctly; hand-formatting text risks producing a file the
loader itself then fails to parse.

### Persistence granularity: whole profile list, once per Save
Every save writes the *entire* profiles list (not just the touched profile) to keep the writer
simple and match "regenerate the whole file" from proposal.md/spec.md - there is no partial-file
patching. The draft/editing service holds the full list it loaded at the start of the `vara
profiles` session and mutates its own copy on each Save; nothing else in the process reloads the
file mid-session, so no other writer can race with it, but the writer's atomic temp-file-then-move
approach also protects a concurrent hand-edit or another `vara profiles` instance from seeing a
half-written file.

### Command/presentation shape
Add `ProfilesCommand.Create(...)` to `Vara.Cli.Commands`, registered in `Program.cs` like every
other command, including a `--config` option matching every other profile-scoped command (e.g.
`ShowCommand`'s `--config`) so `vara profiles` can target an alternate configuration file - both
`IProfileConfigLoader` and `IProfileConfigWriter` are given that same resolved path.
`YamlProfileConfigWriter` is registered in `Program.cs`'s DI container as
`IProfileConfigWriter` alongside the existing `IProfileConfigLoader` registration, the same way
every other infrastructure service is wired up. Supporting presentation code (menu rendering, the
edit-screen loop, source sub-editor) lives under `Vara.Cli.Presentation`/`Vara.Cli.Commands`
following the existing split (commands parse `System.CommandLine` args and orchestrate;
`Presentation/*` renders). Menu/list selection uses `Spectre.Console.SelectionPrompt<T>` over
small CLI-local row types (e.g. a `MenuRow` wrapping either a `Profile` or a sentinel), not over
`Profile`/`Source` directly, so `Vara.Core` stays free of any UI-facing `ToString()`/display
concerns.

**Every prompt over a non-primitive type MUST set an explicit `Converter`.** Both
`SelectionPrompt<T>.Converter` and `TextPrompt<T>.Converter` default to `null`, which makes
Spectre.Console resolve a display string via `T`'s `System.ComponentModel.TypeConverter`
(`TypeConverterHelper.GetTypeConverter<T>()`). Spectre special-cases a fixed list of primitives
(`bool`, `int`, `string`, `double`, etc. - see `TypeConverterHelper`'s intrinsic converter table)
specifically so those don't need reflection; anything else - including any CLI-local row type
such as `MenuRow` or a source-list row - has no such converter and no `[TypeConverter]` attribute,
so the default path throws `InvalidOperationException("Could not find type converter")` the first
time the prompt is shown. This is not an AOT-only risk (it throws in any build configuration); it
must be set explicitly, e.g. `new SelectionPrompt<MenuRow>().UseConverter(row => row.Label)`, for
every prompt instantiated over `MenuRow`, a source-list row type, or any other custom type
introduced by this change. Prompts over `string`, `int`, or `bool` (name/target text entry,
retention/concurrency counts, the recursive flag) need no explicit converter - those are on
Spectre's intrinsic list already.

Non-interactive detection needs both directions, not just one: the existing
`OutputMode.IsLiveCapable` (`console.Profile.Out.IsTerminal`) only answers whether the *output*
side supports a live redraw, which is what it was built for (the backup progress bar). Prompts
additionally need a real keyboard on the *input* side, so `vara profiles` SHALL also check
`Console.IsInputRedirected` at startup (stdin piped or redirected ⇒ report the "requires an
interactive terminal" error immediately, before any prompt is shown) - reusing `OutputMode` alone
would miss a redirected-stdin/normal-stdout case and hang or throw on the first prompt instead of
failing cleanly.

### Known limitation: an already-malformed configuration file blocks `vara profiles` too
`IProfileConfigLoader.LoadProfiles` is all-or-nothing - a single structurally invalid profile, or
unparsable YAML, throws (`ProfileConfigMalformedException`/`ProfileValidationException`/etc.)
before any profile can be returned. `vara profiles` loads through this same interface, so a user
whose hand-edited `profiles.yml` is already broken cannot launch the editor to fix it either - it
fails the same way every other command does today, rather than the new command being able to
rescue a broken file. This is accepted as a deliberate v1 scope boundary, consistent with every
other command's existing behavior, rather than an oversight: teaching the loader (or a new
recovery path) to partially load a file with one bad profile is a meaningfully larger scope
(deciding how to represent/display an entry that can't be parsed at all) and is left for a future
change if it turns out to matter in practice.

## Risks / Trade-offs

- [Regenerating the whole file drops user comments/formatting] → Accepted per proposal.md; this
  is a deliberate simplification, not an oversight. `docs/config-sample.yml` remains the
  reference for anyone who prefers to keep hand-editing instead of using `vara profiles`.
- [Reusing domain constructors for live validation means every keystroke that completes a field
  reconstructs a `Profile`/`Source` object graph] → Profile lists are small (a handful of
  profiles/sources), so the cost is negligible; this also guarantees the editor can never drift
  out of sync with the domain's actual validation rules.
- [A shared uniqueness helper introduces one more small type in `Vara.Application.Profiles`] →
  Kept intentionally tiny (pure function over a name list) to avoid over-engineering a
  three-line check into a larger abstraction.
- [Atomic temp-file-then-move write could still fail on an unusual filesystem/permissions setup] →
  On failure, the system SHOULD surface the underlying I/O error clearly (via the existing
  `ErrorReporting`/`ProfileConfigException` conventions) rather than silently losing the edit; the
  in-memory draft is not cleared until the write has actually succeeded, so the user can retry
  Save.
- [An already-malformed `profiles.yml` cannot be opened/repaired via `vara profiles`] → Accepted
  as a v1 scope boundary (see "Known limitation" decision above); the user's remaining recourse is
  the same as today - hand-fix the file, or delete/recreate it, before `vara profiles` can load it.
