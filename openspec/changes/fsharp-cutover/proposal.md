## Why

Once every capability has an F# implementation that passes its scenario and parity tests, the F# `vara.exe` can replace the C# build. Until then, two implementations are maintained, C# changes are frozen except for bug fixes, and each fix needs a parity test. Ending that cost is the point of this change.

Design and tasks for this change are written by `fsharp-profile-management`'s final task, using the findings of the whole series.

## What Changes

- The F# `vara.exe` becomes the shipped build, and the `portable-distribution` scenarios are verified on it.
- **BREAKING (repository layout only):**
  - remove the C# projects (`Vara.Core`, `Vara.Application`, `Vara.Infrastructure`, `Vara.Cli`) and their test projects;
  - move `src/fsharp/*` to `src/` and `tests/fsharp/*` to `tests/`;
  - update `Vara.slnx`, `Directory.Build.props` and `Directory.Packages.props`, dropping the C#-only settings and packages.

  Users see no change: the executable, commands, output, configuration and on-disk format are the same.
- Decide what happens to the parity suite, which references the C# projects: remove it, or replace it with a recorded corpus of expected outputs. Scenario and property tests remain the long-term suite.
- Move the benchmarks to the F# implementation and record a new reference baseline. Update `benchmarks/baselines/README.md`.
- Update `README.md` and the build and publish instructions.
- Archive the series' changes as needed, and end the C# behavior freeze.

## Capabilities

### New Capabilities

None.

### Modified Capabilities

None. The shipped behavior is unchanged (`skip_specs: true`).

**Owned scenarios:** every `portable-distribution` requirement, verified on the F# executable copied alone into an empty directory.

## Impact

- **Code:** the C# sources and tests are removed, and the F# projects are moved.
- **Distribution:** the published `vara.exe` is the F# build.
- **Benchmarks:** a new F# reference baseline.
- **Dependencies:** requires every other change in the series.
