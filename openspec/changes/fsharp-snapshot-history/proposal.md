## Why

Snapshot history is how users get their data back. It covers listing snapshots, a file's version history, restoring files and directories, showing and diffing versions, inspecting what a snapshot changed, and browsing a point in time. It is the largest capability, with 811 spec lines, and it reads the manifest and content store the engine writes.

Its decisions are mostly pure functions over manifest rows: which version was live at a given moment, how a move chain links a file's history, what a directory looked like, and what a restore must write or remove. That makes it the second large test of the functional design, after the engine.

Design and tasks for this change are written by `fsharp-backup-presentation`'s final task, using the findings of the changes before it.

## What Changes

- Add the history commands to the F# `vara`: `snapshots`, `snapshot`, `history`, `restore` (a file or a directory, at a version or a date, to the original location or elsewhere), `show`, `diff`, `browse` and `deleted`.
- Add to `Vara.Domain` the pure history model:
  - point-in-time state;
  - move-chain history;
  - directory listings with deleted entries;
  - the snapshot change roll-up;
  - restore planning.
- Add the manifest queries and restore effects to `Vara.Engine`, including atomic restore writes and the guard against writing into the live mirror. Restore uses the engine's read-only manifest mode.
- Wire profile resolution from the working directory, which `fsharp-profile-config` built, into the browsing and restore commands.
- Add date/time option parsing with friendly errors and an example value in the help text.
- As its last task, update the following changes with this change's findings.

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
- Each command is a pipeline: resolve the profile, query the manifest, project to a result with pure functions, build a `Display`, render. The commands share these stages and differ only in the projection.
- Point-in-time state, move-chain history, directory listings, the change roll-up and restore planning are pure functions over manifest rows passed in as values. The manifest queries in `Vara.Engine` return rows or a sequence of rows; no query logic hides in the domain code, and no domain logic hides in a query.
- Restore is a plan and then an execution: a pure function produces the list of writes and removals, and the shell performs them. A dry preview of a restore is therefore the plan itself.
- Output follows the view pattern from `fsharp-backup-presentation` (a pure `Display` rendered at the edge). Diffs and listings are built as data, then rendered.
- No `SnapshotService`, `RestoreService` or repository classes. Reuse the engine's manifest functions rather than adding a parallel access layer.

## Capabilities

### New Capabilities

None.

### Modified Capabilities

None. This is a behavior-preserving reimplementation (`skip_specs: true`).

**Owned scenarios:**
- **`snapshot-history`:** every requirement.
- **`backup-browsing`:** every requirement.
- **`cli-presentation`:** "Bordered widgets use a consistent rounded-corner style", "Invalid date/time option input produces a friendly error" and "Date/time options document an example value in their help text".
- **`progress-reporting`:** "Aggregate progress across a multi-file directory restore" and "Restore task labels are bounded to a fixed width".

## Impact

- **Code:** all three F# source projects grow, and DiffPlex is added to `Vara.Cli` (it is already pinned centrally). No changes to the C# projects.
- **Tests:** scenario tests for each owned requirement, and parity tests that run the C# and F# commands against the same targets and compare their output and restored files.
- **Dependencies:** requires `fsharp-backup-presentation`, for the shared presentation conventions and progress rendering.
