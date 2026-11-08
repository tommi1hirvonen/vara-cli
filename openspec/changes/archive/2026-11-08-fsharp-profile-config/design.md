## Context

See proposal.md for why. The starting point is the F# skeleton:
- `Vara.Domain` (pure, FSharp.Core only), `Vara.Engine` (I/O) and `Vara.Cli`;
- one test project per source project;
- the shared settings in `src/fsharp/Directory.Build.props`: `--reflectionfree`, and FS0025/FS3261/FS3511 as errors.

The series-wide decisions are in `fsharp-backup-engine`'s design (D1, D2, D5, D6, D10). This change applies them to profile configuration and records only its own decisions.

The C# behavior to reproduce:
- `YamlProfileConfigLoader` reads the YamlDotNet DOM, compares keys case-insensitively, and stops at the first validation error.
- `ProfileResolver` resolves a profile by name, or walks upward from the working directory to a `.vara\profile.db`.
- The message texts are defined in `ProfileConfigExceptions.cs` and the loader.

## Goals / Non-Goals

**Goals:**

- Every owned `profile-config` scenario (proposal.md) has a test named after it, and the spec-coverage check proves it.
- Every invariant the spec states is guaranteed by the types once a profile exists, so no consumer re-validates.
- Profiles and error messages are identical to C# across a shared YAML corpus.
- Gate A is decided with measurements at the end of this change.

**Non-Goals:**

- Writing the configuration file. That is the profile editor's job, in `fsharp-profile-management`.
- Any command, and the rendering of configuration errors. These come with the first command that loads profiles, in `fsharp-backup-engine`.
- Reporting several validation errors at once. C# reports the first one; reporting all of them would be a behavior change and needs its own change with a spec delta.

## Decisions

### D1. Validation runs on a neutral configuration tree, not on YAML nodes

`Vara.Domain` defines a minimal tree:
- a scalar;
- a sequence of nodes;
- a mapping with case-insensitive key lookup, which is what C# does.

`Vara.Engine` turns the YamlDotNet DOM into this tree. All validation is then a pure function from the tree to `Result`. YAML parse failures and an empty document stay in the engine, because they are about the file, not the profiles.

Alternatives considered:

- **Validating YamlDotNet nodes directly:** rejected. It would put an infrastructure package into `Vara.Domain`, and the tests would have to write YAML text.
- **YamlDotNet's deserializer:** rejected. It is reflection-based and not AOT-safe, and C# avoids it for the same reason.

### D2. Errors are values with the C# wording

A single `ConfigError` union covers every failure:
- the file is not found or is malformed;
- a profile is invalid;
- a name is duplicated;
- a profile is unknown;
- a profile name is required.

A single function renders each case with the exact C# message, so the output of the commands that report these errors later does not change.

Validation stops at the first error, which matches C#. A small `result { }` computation expression in `Vara.Domain` and a list-traversal helper make that flow read sequentially. FSharp.Core has no built-in builder for `Result`.

Alternatives considered:

- **Exceptions, as in C#:** rejected. The control flow across the parser, the validation and the resolver is exactly what this change sets out to remove.
- **Collecting every error at once:** rejected for now; see Non-Goals.

### D3. Invariants are guaranteed by construction

A `Profile` can only be produced by validation, and its parts carry their guarantees:
- **Paths:** a canonical absolute path type. It is fully qualified, normalized with `Path.GetFullPath`, and keeps a single trailing separator.
- **Sources:** at least one source, with no two overlapping.
- **Target:** does not overlap any of the profile's own sources.
- **Counts:** a non-negative retention count and a positive concurrency value. Each concurrency setting is optional; when it is absent, the engine uses its own default.

Code that receives a `Profile` never checks any of this again.

Resolution returns one of two shapes:
- a configured profile, resolved by name;
- a target root discovered from the working directory.

Commands that need sources accept only the first. Browsing commands accept either. This replaces C#'s placeholder source and its `validateSourceOverlap: false` flag.

Path canonicalization and overlap checks only manipulate strings: `System.IO.Path` is used for its string functions and touches no files. That keeps them inside `Vara.Domain`'s no-I/O rule.

Configuration is a cold path, so `option` is used for optional fields. The engine's `voption` rule (`fsharp-backup-engine` D6) is for values created per file.

### D4. The engine provides the configuration location and resolution

`Vara.Engine` gets:
- the default `~/.vara/profiles.yml`, with an alternate path that the CLI's `--config` option will supply;
- loading: whether the file exists, reading it, parsing YAML, then validating;
- resolution by name, matched case-insensitively;
- resolution from a start directory by walking upward to `.vara\profile.db`.

Each returns `Result<_, ConfigError>`.

### D5. Spec coverage is checked mechanically, for every change in the series

A new test-support project, `tests/fsharp/Vara.Testing`, holds a check that:
- reads the `### Requirement:` and `#### Scenario:` headings of a spec file;
- compares them with the test methods of a test assembly, found by reflection;
- fails on any owned scenario without a test.

Some scenario titles repeat under different requirements. For example, "Case and trailing separator differences still detected as overlap" appears twice in `profile-config`. So the convention is:
- each test method is named after the scenario title;
- it sits in a module named after the requirement title, both as double-backtick identifiers;
- coverage is matched on the requirement and the scenario together.

### D6. Parity with C# is compared on a shared YAML corpus

`Vara.Engine.Tests` references the C# `Vara.Infrastructure` project and loads the same YAML files with `YamlProfileConfigLoader` and with the F# loader. The two must give either equal profiles (compared field by field) or equal messages (the C# exception message against the F# rendered error).

The corpus covers:
- every owned scenario;
- the edge cases of the C# loader tests: key casing, `recursive` parsing, an empty sequence, null scalars, and a non-mapping entry under `profiles` or `sources`.

### D7. Gate A is decided on recorded measurements

At the end of this change, the commit message records:

1. **Code lines** (excluding comments and brace-only lines) of the F# configuration code, against the 321 lines in C#. The target is at most 75%.
2. **Runtime validation outcomes and bypasses** in each language: exceptions thrown for validation and caught again, flags that bypass a rule, and consumers that re-check an invariant. The target is none in F#.
3. **Parity and coverage:** the parity suite is green and every owned scenario is covered.
4. **Idiom:** the convention review from the proposal's "Functional steering" passes. There are no classes, interfaces or `mutable` in `Vara.Domain`, no dependency passed as an object, and no module named after a role (`Loader`, `Resolver` as classes, `Helper`, `Service`). A change that is fewer lines but still shaped like the C# code does not pass.

The reviewer then reads both implementations side by side and decides whether F# is clearly simpler and idiomatic.

- **If it passes:** the series continues with `fsharp-backup-engine`.
- **If it fails:** the series stops as described in the migration roadmap, and the findings are recorded in `fsharp-backup-engine` before that change is abandoned.

## Risks / Trade-offs

- [Matching C#'s error wording ties the F# code to the old text] → The text only needs to match until the cutover; after that it can change through ordinary changes. The rendering is one function, so changing it later is cheap.
- [YamlDotNet node types other than mapping, sequence and scalar, such as aliases] → They are mapped to the description C# uses ("an unsupported node type") and rejected the same way, and the parity corpus includes one.
- [Gate A is partly a judgment call] → The three measurements are recorded first, so the judgment has numbers to rest on.

## Migration Plan

Nothing ships, and no command uses the new code until `fsharp-backup-engine`. To roll back, revert this change's commits.
