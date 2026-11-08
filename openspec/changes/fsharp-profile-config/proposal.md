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
