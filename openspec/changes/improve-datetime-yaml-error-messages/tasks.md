## 1. Date/time option parsing

- [x] 1.1 Add `InvalidDateTimeOptionException(string optionName, string value)` (exposing `OptionName`/`Value`) alongside `ErrorReporting` in `src/Vara.Cli/Composition/`, and verify the solution builds.
- [x] 1.2 Add `DateTimeOptionParser.Parse(string optionName, string value)` in `src/Vara.Cli/Composition/` that calls `DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal)` and throws `InvalidDateTimeOptionException` on `FormatException`, and add a unit test asserting a bad value throws it with the expected `OptionName`/`Value` while a valid value still returns the parsed `DateTimeOffset`.
- [x] 1.3 Register `InvalidDateTimeOptionException` in `ErrorReporting.TryGetFriendlyMessage`'s switch, and add a test asserting `ErrorReporting.Run` reports its friendly message (not the catch-all) and exit code 1.
- [x] 1.4 Replace the six raw `DateTimeOffset.Parse(...)` call sites with `DateTimeOptionParser.Parse("--at", ...)` / `"--since"` / `"--left-at"` / `"--right-at"` in `RestoreCommand.cs` (both call sites), `BrowseCommand.cs`, `ShowCommand.cs`, `DeletedCommand.cs`, and `DiffCommand.cs` (both call sites), passing each option's own flag name, and verify the solution builds.

## 2. Malformed YAML detection

- [x] 2.1 Wrap the `yamlStream.Load(reader)` call in `YamlProfileConfigLoader.LoadProfiles` in a try/catch for `YamlDotNet.Core.YamlException`, rethrowing `ProfileConfigMalformedException(configPath, $"could not be parsed as YAML - {ex.Message}")`.
- [x] 2.2 Add a test with a fixture `profiles.yml` containing a real YAML syntax error (for example, a mapping value with inconsistent indentation) asserting `LoadProfiles` throws `ProfileConfigMalformedException` (not `YamlException`) with `ConfigPath` matching the file path.

## 3. Help text examples

- [x] 3.1 Append an example date/time value (for example, `(for example, '2025-01-15' or '2025-01-15 14:30')`) to the existing `Description` of the `--at` option in `RestoreCommand.cs`, `BrowseCommand.cs`, and `ShowCommand.cs`, the `--since` option in `DeletedCommand.cs`, and the `--left-at`/`--right-at` options in `DiffCommand.cs`.
- [x] 3.2 Verify each affected command's `-h`/`--help` output shows the new example text (manual check or an existing help-output test, if one exists for these commands).

## 4. End-to-end verification

- [x] 4.1 Run the full `Vara.Cli.Tests` and `Vara.Infrastructure.Tests` suites and verify they pass.
- [x] 4.2 Manually run `vara restore x --at yesterday` (or another affected command) against a real profile and verify it prints the new friendly error and exit code 1, with no raw stack trace.
- [x] 4.3 Manually run a command against a hand-edited `profiles.yml` containing a YAML syntax error and verify it prints the configuration error from `ProfileConfigMalformedException`, with no raw stack trace.
