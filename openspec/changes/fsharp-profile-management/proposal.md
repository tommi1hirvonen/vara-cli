## Why

`vara profiles` is the interactive editor for the profile configuration. It lists profiles, and adds, edits and deletes them through a draft that is validated live and saved only when valid. Saving regenerates the whole configuration file and writes it durably before it replaces the old one.

It is the last command to migrate. It reuses the profile model and validation from `fsharp-profile-config`, and adds the one thing that change left out: writing the configuration file.

Design and tasks for this change are written by `fsharp-retention-and-integrity`'s final task, using the findings of the changes before it.

## What Changes

- Add `vara profiles` to the F# `vara`:
  - the main menu;
  - profile fields editable in any order;
  - sources managed as a sub-list;
  - live validation of the draft after every edit;
  - save, discard, and delete with confirmation;
  - interruption without persisting.
- Model the draft as a pure state machine in `Vara.Domain`. Its live validation reuses the `fsharp-profile-config` validation, so the editor and the loader cannot disagree.
- Add a configuration writer to `Vara.Engine`. It regenerates the YAML, and writes durably: write a temporary file, flush it, then replace the old file. A failed write can be retried within the same session.
- As its last task, update `fsharp-cutover` with this change's findings.

There are no user-visible changes: the shipped `vara.exe` is still the C# build.

## Functional steering

This change is idiomatic F#, not a translation of the C# code. C# type and method names are not a guide: design from the spec. Each change in the series is applied in a separate session, so every proposal carries these rules.

**Series-wide rules**
- **Modules and data, not classes.** Behavior is functions in modules; data is records and unions. No interfaces, inheritance, service classes or dependency injection. A class is allowed only where .NET requires one: handle wrappers, `IDisposable` resources, and types that a host library demands.
- **Dependencies are parameters.** A function takes the functions it needs, not an object that holds them. Effects that travel together may be grouped as a record of functions, only when three or more are used together. Everything is assembled once, by partial application in the composition root (`Vara.Cli`).
- **Compose, don't sequence.** Build behavior from small functions joined with `|>`, `Result`, `Option` and `Seq` combinators, and a computation expression where a flow reads in order. Put the data argument last so that partial application works.
- **Types carry the rules.** Unions for alternatives, `[<RequireQualifiedAccess>]` on them, records with named fields, single-case unions or units of measure for values that could be confused. Matches are exhaustive with no wildcard on the code's own unions; no `null`; invalid states are unrepresentable.
- **Expected outcomes are values.** Use `Result` or `option` for them. Exceptions are for the unexpected, and are handled at the host boundary.
- **Pure core, effect shell.** Decisions never touch the file system, the clock, randomness or the console. The shell passes in values and performs the effects.
- **Mutation stays inside one function.** It is allowed in a hot loop, a pooled buffer or interop, when a benchmark or the API requires it. It never lives in a field and is never shared between functions. Allocation rules (struct unions, `voption`) change a value's representation, not the architecture.
- **No catch-all modules.** No `Helper`, `Manager`, `Service`, `Util` or `I`-prefixed names. One module per concept, with files in dependency order.
- **Review.** The change's tasks include a convention review, and its last task keeps this section in the proposals that follow, extended with what the change learned.

**Specific to this change**
- The editor is an Elm-style loop: `update : State -> Input -> State * Effect`, a pure `view : State -> Display`, and a thin loop that reads an input, calls `update`, performs the returned effect and renders. Effects (save, confirm, quit) are data that the loop interprets, so the whole editor is testable without a console.
- The draft state is a union of screens (main menu, editing a profile, editing a source, confirming a delete), not a bag of flags. Live validation is the `fsharp-profile-config` function applied to the draft.
- The writer is split in two: a pure function from profiles to a configuration tree and YAML text, and a small durable-write function (temporary file, flush, replace). A failed write is an effect result that `update` handles, which is how retry works.
- No `ProfileEditor`, `ProfileMenu` or prompt-wrapper classes. Spectre prompts are called from the loop's input function only.

## Capabilities

### New Capabilities

None.

### Modified Capabilities

None. This is a behavior-preserving reimplementation (`skip_specs: true`).

**Owned scenarios:** every `profile-management` requirement.

## Impact

- **Code:** all three F# source projects grow. No changes to the C# projects.
- **Tests:** scenario tests driven through `Spectre.Console.Testing` input, and a parity test showing that a file saved by the F# editor loads the same in the C# loader, and the other way round.
- **Dependencies:** requires `fsharp-retention-and-integrity`, as the previous change in the series. Functionally it needs only `fsharp-profile-config` and the presentation conventions from `fsharp-backup-presentation`.
