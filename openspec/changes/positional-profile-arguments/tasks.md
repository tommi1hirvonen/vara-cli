## 1. CLI Command Updates

- [ ] 1.1 In `src/Vara.Cli/Commands/BackupCommand.cs`, replace `Option<string>("--profile")` with a required `Argument<string>("profile")`, remove the `--profile` option, and verify the project builds with `dotnet build src/Vara.Cli`
- [ ] 1.2 In `src/Vara.Cli/Commands/PruneCommand.cs`, replace `Option<string>("--profile")` with a required `Argument<string>("profile")`, remove the `--profile` option, and verify the project builds with `dotnet build src/Vara.Cli`
- [ ] 1.3 In `src/Vara.Cli/Commands/CheckCommand.cs`, replace `Option<string>("--profile")` with a required `Argument<string>("profile")`, remove the `--profile` option, and verify the project builds with `dotnet build src/Vara.Cli`
- [ ] 1.4 In `src/Vara.Cli/Commands/SnapshotsCommand.cs`, replace `Option<string?>("--profile")` with an optional `Argument<string?>("profile")` (`Arity = ArgumentArity.ZeroOrOne`), remove the `--profile` option, and verify the project builds with `dotnet build src/Vara.Cli`

## 2. CLI Test Updates

- [ ] 2.1 In `tests/Vara.Cli.Tests/Commands/BackupCommandTests.cs`, update test invocations to pass the profile as a positional argument, assert existing tests pass, and add test cases asserting that missing the profile positional argument fails and passing `--profile` fails as an unrecognized option
- [ ] 2.2 In `tests/Vara.Cli.Tests/Commands/PruneCommandTests.cs`, update test invocations to pass the profile as a positional argument, assert existing tests pass, and add test cases asserting that missing the profile positional argument fails and passing `--profile` fails as an unrecognized option
- [ ] 2.3 In `tests/Vara.Cli.Tests/Commands/CheckCommandTests.cs`, update test invocations to pass the profile as a positional argument, assert existing tests pass, and add test cases asserting that missing the profile positional argument fails and passing `--profile` fails as an unrecognized option
- [ ] 2.4 In `tests/Vara.Cli.Tests/Commands/`, add unit test coverage for `SnapshotsCommand` verifying that `vara snapshots <profile>` passes the explicit profile name to `ProfileResolver.ResolveForBrowsing`, `vara snapshots` passes `null` to trigger CWD detection, and passing `--profile` fails as an unrecognized option

## 3. Solution Verification and Documentation

- [ ] 3.1 Run `dotnet test` across all test projects to verify that all unit and integration tests pass without regression
- [ ] 3.2 Update `README.md` to reflect the new positional profile syntax for `backup`, `prune`, `check`, and `snapshots` in the command summary table and usage narrative, verifying that path-based commands remain documented with `--profile`
