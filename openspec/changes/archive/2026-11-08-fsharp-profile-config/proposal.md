## Why

`vara backup` and every later F# command need a validated profile, so profile configuration comes first in the migration series (see `fsharp-backup-engine`'s design, "Migration roadmap"). It is also the cheapest place to settle the series' first decision point, **Gate A**. Rule-heavy validation code is where a functional design should pay off most clearly, so the series continues only if this change is measurably simpler than its C# counterpart.

The C# implementation (`YamlProfileConfigLoader`, `ProfileResolver`, `Profile`/`Source`, `RetentionPolicy`, `ConcurrencySettings`, `ProfileNameUniqueness` and eight configuration exception types) totals 321 code lines, excluding comments and brace-only lines. It shows the costs a functional design avoids:
- validation outcomes are exceptions, caught and rethrown across three layers;
- constructors throw `ArgumentException`s that the loader translates by parameter name;
- a `validateSourceOverlap: false` flag lets the working-directory resolver build a profile whose placeholder source deliberately violates the overlap rule.

## What Changes

- Add the profile model to `Vara.Domain`. A configured profile and a profile discovered from the working directory are different types, so the placeholder source and the overlap-bypass flag disappear.
- Validate as a pure function from a neutral, YAML-independent configuration tree to `Result` values:
  - required fields, list shapes, absolute paths and their canonical form;
  - target/source and source/source overlap;
  - duplicate names;
  - retention and concurrency values.

  Error messages match the C# wording, so a later command's output stays the same.
- Add to `Vara.Engine`:
  - YAML parsing into the neutral tree (YamlDotNet DOM API; the file location and malformed-file rules stay here);
  - the default `~/.vara/profiles.yml` location and the alternate path;
  - profile resolution by name, and by walking upward from the working directory to a `.vara\profile.db`.
- Add a reusable spec-coverage check to the F# test support. It fails when a scenario a change owns has no test with the same name, and every later change in the series uses it.
- As its last task, update the following changes with this change's findings.

There are no user-visible changes. No command uses the new code yet: `vara backup` arrives in `fsharp-backup-engine`.

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
- The loader and the resolver are modules, not classes. Loading is a pipeline: locate, read, parse to the neutral tree, validate, resolve.
- Validation is built from small field validators of the form `node -> Result<'a, ConfigError>`, combined in `result { }`. There is no validator class hierarchy, no rule registry and no parameter-name-based error translation.
- This change sets the series' first conventions in code (the `result` builder, the path and profile types, the coverage check). Later changes copy their style, so keep it small and plain.

## Capabilities

### New Capabilities

None.

### Modified Capabilities

None. This is a behavior-preserving reimplementation (`skip_specs: true`). The `profile-config` spec is the acceptance contract.

**Owned scenarios:** every `profile-config` scenario except "Profile without a retention policy", which needs the prune command and is owned by `fsharp-retention-and-integrity`. Scenarios whose outcome is a command reporting an error or doing nothing are verified at the resolution boundary: the function returns the error value that a command reports, and no action can start without a profile. The commands that render these errors are tested in the changes that add them.

## Impact

- **Code:**
  - `src/fsharp/Vara.Domain` gains the profile model and validation;
  - `src/fsharp/Vara.Engine` gains YAML parsing, the configuration location and profile resolution, plus the YamlDotNet package, which is already pinned centrally;
  - no changes to the C# projects.
- **Tests:**
  - `tests/fsharp/Vara.Domain.Tests` gains validation tests and properties;
  - `tests/fsharp/Vara.Engine.Tests` gains parsing and resolution tests, and a parity suite that runs the C# `YamlProfileConfigLoader` and the F# loader on the same YAML corpus, requiring the same profiles or the same error message;
  - a new `tests/fsharp/Vara.Testing` project holds the spec-coverage check.
- **Gate A:** decided at the end of this change (see design.md).
