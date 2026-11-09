## Context

See proposal.md for why. This design builds on `fsharp-retention-and-integrity`, which is applied before it, and reuses what the earlier changes established instead of defining it again.

What is in place, and what this design uses:

- **Domain** (`Vara.Domain`): `Profile` (private record; `Profile.create` enforces sources, overlap and the target rule), `Source`, `RetentionPolicy`, `RetentionCount`, `Concurrency`, `ConcurrencyLimit`, `ConfigNode` (the configuration as a tree), `ConfigError` with `ConfigError.render`, `ProfileValidation.validateProfiles : ConfigNode list -> Result<Profile list, ConfigError>`, `Outcome`. Presentation: `Display` (`Line.Text`, `Spans`, `Table`, `Box`, `Tone`), `Display.plainLines`, `Capabilities`, `OutputMode`.
- **Engine** (`Vara.Engine`): `YamlConfig.parse` (text to `ConfigNode list`), `ConfigFile.locate` and `ConfigFile.load`. There is no writer.
- **Cli** (`Vara.Cli`): `CliEnv` (consoles, `Capabilities`, `Prompts`, `ErrorConsole`, `CurrentDirectory`), `Prompts` (`Select`, `Confirm`), `HistorySession` (`fail`, `show`, `configOption`, `textOf`), `CommandAction.set`, `GracefulCancellation`, `LiveRenderer`, `PlainRenderer`, `Terminal`, `ErrorReporting`.
- **Tests**: `Vara.Testing.SpecCoverage`, the in-process command harness (`invokeEnv`, `scratch`, `asking`, `notAsking`, scripted `Prompts`), and the process parity pattern.

The C# code is the behavioral reference, not a design: `ProfilesCommand` (737 lines), `ProfileDraft`, `SourceDraft`, `ProfileDraftPresenter` and `YamlProfileConfigWriter`.

## Goals / Non-Goals

**Goals:**

- Every owned scenario is verified by a test named after it. The state machine and the writer are tested on plain data before a console or a file is involved.
- The editor and the loader cannot disagree: live validation, save validation and loading are one function.
- A configuration write is atomic and durable, and a failed write leaves the session running with the draft intact.
- A file written by the F# editor loads identically in the C# loader and the other way round.

**Non-Goals:**

- Preserving comments or key order in the configuration file (the spec says they need not survive).
- A `--json` mode or any non-interactive editing.
- Changes to the profile model or the validation rules. A difference found while porting is recorded as a C# bug fix with a parity test first.

## Decisions

### D1. The editor is an Elm-style loop over plain data

`update : EditorState -> Input -> EditorState * Effect list` and `view : EditorState -> Display` are pure and live in `Vara.Domain` (`ProfileEditor`, `ProfileEditorView`). The state is a union of screens, not a bag of flags:

- `MainMenu`;
- `EditingProfile` (the draft, the original name if editing, the last validation result);
- `EditingSource` (the draft and the index of the source);
- `ConfirmingDelete` (the profile);
- `Exited`.

The saved profiles are part of the state, as a list of `Profile`. `Input` is a union of what a user can do on a screen (select a menu entry, set a field to a text, add, edit or remove a source, save, discard, confirm, decline, interrupt). `Effect` is a union the loop interprets: `Persist of Profile list` (for save and delete), `Quit`. A write result comes back to `update` as an input (`Persisted` or `PersistFailed of ConfigWriteError`), which is how a failed write stays on the same screen with the draft intact and how a retry needs no re-entry. Alternatives: a mutable editor object with methods per screen (rejected by the series rules); effects performed inside `update` (rejected: the whole editor must run without a console).

### D2. A draft holds text; validation is the loader's function

A `ProfileDraft` is a record of the user's raw entries: name, target, retention and concurrency counts as text or not set, and a list of `SourceDraft` (path, recursive text or flag, three lists). Fields not yet set are `None`, which the view shows as "not set" apart from an empty string. A pure `ProfileDraft.toNode : ProfileDraft -> ConfigNode` builds the same tree the YAML reader would have produced for the draft, and `ProfileDraft.validate savedNodes original draft` applies `ProfileValidation.validateProfiles` to the saved profiles' nodes with the draft's node added (or replacing the original). This gives, with no second set of rules:

- the same required-field, absolute-path, overlap, retention and concurrency checks as the loader (D3 of `fsharp-profile-config`);
- the duplicate-name check against the other saved profiles, with the rename-to-itself exception, because the original's node is replaced and not added;
- locale-independent numbers, because counts stay text until the loader's invariant parser reads them;
- no stale validity: the result is computed from the current draft by `update` after every input, and Save computes it again at that moment and persists only the profile that this run produced.

A source is part of the draft from the moment it is added, as an empty `SourceDraft`, so it makes the draft invalid until its path is set. The validated `Profile` is an immutable value, so later edits to the draft cannot alter it. Alternatives: a second validation written against the draft type (rejected: it could drift from the loader); validating only on Save (rejected by the spec).

### D3. The writer is a pure tree plus a durable file write

Three functions, each testable on its own:

- `ProfileNodes.ofProfiles : Profile list -> ConfigNode list` (pure, in `Vara.Domain`): the tree for every profile with the keys the loader reads (`name`, `target`, `sources` with `path`, `recursive`, `exclude`, `include_globs`, `exclude_globs`, `retention` with the four counts, `concurrency` with `scan_concurrency` and `transfer_concurrency`), omitting what is not set. A property test checks `validateProfiles (ofProfiles profiles)` returns the same profiles.
- `YamlConfig.render : ConfigNode list -> string` (in `Vara.Engine`, with YamlDotNet): the document text. No comments and no preserved order.
- `ConfigFile.writeDurably : string -> string -> Result<unit, ConfigWriteError>`: create the directory, write a temporary file in the same directory, flush it to disk (`FileStream.Flush(true)`), then `File.Replace` over an existing file or `File.Move` for the first write, delete the temporary file in a `finally`. An `IOException` or `UnauthorizedAccessException` is a `ConfigWriteError` value (the path and the reason) and never an exception; any other exception leaves with its stack. `ConfigWriteError` renders in the same style as the other configuration errors, with no stack trace.

Alternative: patching the existing file in place (rejected: the spec regenerates it).

### D4. The loop is the only caller of the prompts and the screen

A function `ProfilesEditor.run` in `Vara.Cli` reads an input from the user, calls `update`, interprets the effects and shows `view`. Its effects are a record of functions added to `CliEnv`: `Prompts` (extended with `Text : string -> string option -> string option` for a free-text entry with a default and `Select`/`Confirm` as they are), `Screen` (enter the alternate screen, clear, leave it) and `Persist : string -> Profile list -> Result<unit, ConfigWriteError>`. The renderers draw a `Display` after a clear, so no earlier copy of a screen remains. Alternate-screen control sequences are written only by `LiveRenderer` or `Terminal`; the loop restores the primary screen in a `finally`, on a quit, an error and an interrupt alike.

The command refuses to start unless `Capabilities.InputIsInteractive` holds and the output is a terminal; the refusal is a hard error that says the command requires an interactive terminal and changes nothing. A missing configuration file is an empty list of profiles, not an error, and the first save creates it.

### D5. An interrupt exits promptly and persists nothing

The first Ctrl+C is suppressed by `GracefulCancellation`, which a blocked prompt would never see. The command registers on its token a callback that restores the primary screen and ends the process with the interrupt exit code; no draft is written because nothing is written except by an `Effect.Persist`, which is atomic (D3), and a profile saved earlier in the session is already on disk. The pure state machine also accepts `Interrupt` as an input that returns `Exited` with no effect, so the rule is tested without a console. Whether the process exit can be exercised in a test is checked first (Open Questions).

### D6. The view is `Display`, built from existing vocabulary

The main menu is a `Table` (name, target, source count) with the actions as entries; the edit screen is a `Box` titled with the profile's name holding the field rows, a "not set" tone for unset fields, the sources as a `Table`, and the validation error in the error tone. Every screen is a function of the state, so each re-display is complete after a clear. Colours come from `Tone`s, never from Spectre types. Layout constants that the spec does not fix come from `ProfileDraftPresenter` and are named with a comment.

### D7. Parity is checked on files, in both directions

A test writes profiles with the F# writer and loads the file with the C# loader (`YamlProfileConfigLoader`, reachable from `Vara.Engine.Tests`, which references `Vara.Infrastructure`), and the reverse: the C# writer's file loaded by the F# loader. Both compare the profiles' fields, not the text. A process parity test is not possible for the interactive editor (a child process cannot be driven through prompts), so the editor rests on in-process tests with scripted prompts.

## Risks / Trade-offs

- [The editor and the loader disagree on a rule] → Both call `validateProfiles`; a property test generates drafts and checks that a draft the editor calls valid loads from the file it saves.
- [A Ctrl+C during a prompt leaves the terminal in the alternate screen] → Restoration is in a `finally` and in the cancellation callback; a test with a screen effect that records its calls checks the pairing for a quit, an error and an interrupt.
- [`File.Replace` behaves differently from the C# write on a read-only file] → Tests on a real read-only file and a denied directory assert the previous contents survive and the error names the file; the first-write case uses `File.Move`.
- [Spectre's text prompt does not publish under Native AOT] → The publish task runs a probe of the prompt before the editor is built on it; if it warns, a minimal key-reading prompt in `Prompts` replaces it without changing the record.
- [The extended `CliEnv` breaks existing test constructors] → Every record literal in the test helpers is updated in the same step, and every existing command test must pass unchanged.

## Migration Plan

Nothing ships: the C# `vara.exe` stays the distributed build. To roll back, revert this change's commits. The edits to `CliEnv`, `Prompts` and the renderers alter code that earlier changes added; their tests stay green and are rerun in the final task. This change adds no code to the scan, the executor or the pipeline, so the benchmark task is a `git diff` check.

## Open Questions

- Whether the process exit of D5 can be driven in an in-process test or needs a process-level test with a console-control signal. It does not change the specs or the design; the task that adds it decides and records the answer.
