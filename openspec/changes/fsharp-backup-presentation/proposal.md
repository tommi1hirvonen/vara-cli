## Why

After `fsharp-backup-engine`, `vara backup` runs the F# engine but prints only an interim plain-text summary. This change gives the F# backup command the output users get from the C# build:
- the live progress display;
- the run summary and its outcome styling;
- the dry-run display;
- JSON output;
- the Windows Terminal progress indicator;
- sleep prevention during a run.

After this change, F# `vara backup` is complete. That makes it the place for the series' second decision point, **Gate B**: the AOT-compiled F# backup measured against the C# one on a quiet machine.

Design and tasks for this change are written by `fsharp-backup-engine`'s final task, using its findings.

## What Changes

- Add live backup progress to `Vara.Cli`, as a projection of the engine's run state:
  - the scan and transfer phases;
  - byte, count, throughput and ETA fields that keep a fixed width;
  - the active-transfer list;
  - rate-limited, never-regressing updates;
  - a heartbeat during stalls;
  - the outcome-styled final bar.
- Add the run summary, the dry-run display and `--json` output for real and dry runs.
- Add the shared presentation conventions:
  - outcome styles and exit codes;
  - plain-text fallback for redirected output, `NO_COLOR` and terminals without color support;
  - hard-error rendering;
  - the Windows Terminal progress indicator.

  Later changes reuse them.
- Add sleep prevention through the Win32 power-request API. A failure to create or clear the request never blocks the backup.
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
- Presentation is a pure view over state: `view : RunState -> Display`, where `Display` is plain data (lines, fields, styles, widths). Throughput, ETA, rate limiting and the never-regressing rule are pure functions of the state and a clock value passed in. None of this references Spectre.Console.
- Spectre.Console is touched in one place only, a thin renderer at the edge that interprets `Display`, and a plain-text renderer interprets the same value. Tests assert on `Display` values first, and on rendered output only for the renderers.
- No renderer or progress-reporter classes. The engine receives a plain `Progress -> unit` function. Outcome styling and exit codes are a total function from `Outcome`.
- Sleep prevention is a bracket function, `withSleepPrevention : (unit -> Task<'a>) -> Task<'a>`, not a service. A failure to create or clear the request is a value that the bracket ignores.
- JSON output is built from the same run result as the display, by a pure function to a document, written once.

## Capabilities

### New Capabilities

None.

### Modified Capabilities

None. This is a behavior-preserving reimplementation (`skip_specs: true`).

**Owned scenarios:**
- **`progress-reporting`:** every requirement except "Count-based progress indication for non-byte-transfer operations" (owned by `fsharp-retention-and-integrity`), and "Aggregate progress across a multi-file directory restore" and "Restore task labels are bounded to a fixed width" (owned by `fsharp-snapshot-history`).
- **`cli-presentation`:** "Outcome severity is visually distinct", "Outcome severity determines process exit code", "Non-interactive or color-incapable output falls back to plain text", "Unclassified exceptions are rendered in the hard-error style" and "Machine-readable JSON output for the backup command".
- **`backup-execution`:** "Automatic system sleep is prevented during a real backup" and "Sleep-prevention request failure does not block backup".

## Impact

- **Code:** `src/fsharp/Vara.Cli` gains the presentation code; `src/fsharp/Vara.Engine` gains power-request interop. `src/fsharp/Vara.Domain` may gain pure progress calculations (throughput, ETA, rate limiting). No changes to the C# projects.
- **Tests:** presentation tests use `Spectre.Console.Testing`. A parity check compares the C# and F# JSON output of the same run.
- **Gate B:** both `vara.exe` builds are published with Native AOT and measured on a quiet machine:
  - wall-clock time for a first backup, a "nothing changed" incremental backup and a large tree (100k+ files) must be within ±5% of C#;
  - peak memory and executable size are recorded and must be within a margin agreed when the design is written;
  - there must be zero AOT warnings.

  If Gate B fails, the series stops as described in `fsharp-backup-engine`'s migration roadmap.
- **Dependencies:** requires `fsharp-backup-engine`.
