## 1. Preparation

- [ ] 1.1 Create `tests/fsharp/Vara.Testing` and add it to `Vara.slnx`. It holds the spec-coverage check from design D5: parse a spec's requirement and scenario headings, discover test methods by reflection, and report owned scenarios with no test of the same requirement and scenario name. Verify with tests that run it against a small sample spec and a sample assembly, covering a covered scenario, a missing one, and a title repeated under two requirements.
- [ ] 1.2 Add YamlDotNet to `Vara.Engine`. In `Vara.Engine.Tests`, add references to the C# `Vara.Infrastructure` project (for the parity suite) and to `Vara.Testing`. Verify that `dotnet build Vara.slnx` succeeds and that `dotnet list src/fsharp/Vara.Domain package` still shows only FSharp.Core.

## 2. Pure model and validation (`Vara.Domain`)

- [ ] 2.1 Add the `result { }` computation expression and a list-traversal helper (design D2). Verify with tests for short-circuiting on the first error and for traversal order.
- [ ] 2.2 Add the profile model (design D3): the canonical absolute path, sources with at least one entry, the retention policy with non-negative counts, the optional positive concurrency values, and the two resolution shapes (configured profile, discovered target root). Verify that `Vara.Domain` builds and that `git grep -n "invalidOp\|failwith\|raise" -- src/fsharp/Vara.Domain` returns nothing.
- [ ] 2.3 Add the neutral configuration tree, with case-insensitive key lookup, and `ConfigError` with its message rendering (design D1, D2). Verify with tests that each error case renders exactly the message text the C# exception types produce.
- [ ] 2.4 Add validation from the tree to `Result<Profile list, ConfigError>`: required fields, the shape of list fields, `recursive` parsing, absolute and canonical paths, target/source and source/source overlap, duplicate names, retention counts and concurrency values. Verify with one test per owned scenario under "Profile structure validation", "Source and target paths must be absolute", "Target root must not overlap a profile's own sources", "A profile's own sources must not overlap each other", "Duplicate profile names rejected", "Retention policy configuration" and "Concurrency configuration". Each test is named after its scenario inside a module named after its requirement (design D5).
- [ ] 2.5 Add FsCheck properties: canonicalizing a path twice gives the same result as once; overlap is symmetric, and ignores case and a trailing separator; a profile produced by validation never has two overlapping sources, nor a target that overlaps a source.

## 3. Configuration file and resolution (`Vara.Engine`)

- [ ] 3.1 Add YAML parsing into the neutral tree, with the file-level rules. Verify with tests for every scenario under "Malformed configuration file detection", including an empty file, a file with only comments, and a document without a `profiles` key.
- [ ] 3.2 Add the configuration location and loading (design D4). Verify with tests for "Default location used" and "Configuration file missing".
- [ ] 3.3 Add resolution by name and from the working directory (design D4). Verify with tests for every scenario under "Profile selection by name", using scratch directories that contain a `.vara\profile.db` file.

## 4. Parity, coverage and Gate A

- [ ] 4.1 Write the parity suite (design D6): load the shared YAML corpus with the C# `YamlProfileConfigLoader` and with the F# loader, and require equal profiles or equal error messages. Verify that `dotnet test tests/fsharp/Vara.Engine.Tests` passes.
- [ ] 4.2 Add the coverage test for `openspec/specs/profile-config/spec.md`, excluding "Profile without a retention policy" (owned by `fsharp-retention-and-integrity`). Verify that it passes, and that it fails when one scenario test is temporarily renamed.
- [ ] 4.3 Final verification: `dotnet build Vara.slnx` reports no FS or CS warnings; `dotnet test Vara.slnx` passes for both the C# and F# suites; and `dotnet publish src/fsharp/Vara.Cli -c Release -r win-x64` still reports no IL2xxx/IL3xxx warnings.
- [ ] 4.4 Record the Gate A measurements from design D7 in the commit message: code lines against C#'s 321, runtime validation outcomes and bypasses in each language, and parity and coverage results. Ask the reviewer for the Gate A decision, and record it.

## 5. Update the following changes

- [ ] 5.1 Update `fsharp-backup-engine` with this change's findings, using the `openspec-update-change` workflow:
  - it uses this change's profile model and resolution, and its own spec-coverage check comes from `Vara.Testing`;
  - its task that adds C# project references to `Vara.Engine.Tests` only needs to add `Vara.Application`;
  - any new F# lessons go into its D5 and D6.

  If Gate A failed, record the decision there instead and stop the series. Revise the proposals of the later changes only if their scope, dependencies or gates have shifted, and confirm that every scenario in `openspec/specs` is still owned by exactly one change in the series. Verify that `openspec validate` passes for every changed change.
