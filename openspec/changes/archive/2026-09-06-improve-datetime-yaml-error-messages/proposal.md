## Why

Commands that accept a date/time option (`--at`, `--since`, `--left-at`, `--right-at`) parse it
with a raw `DateTimeOffset.Parse` call, and `ErrorReporting.TryGetFriendlyMessage` does not
recognize `FormatException`; an unparseable value therefore falls through to the generic
"Unhandled exception" catch-all instead of a friendly, actionable message. Separately, a
hand-edited `profiles.yml` with a YAML syntax error - the most likely real-world authoring
mistake - throws a raw `YamlDotNet` parse exception from `YamlStream.Load`, before the loader's
own shape checks ever run, so it is never wrapped in `ProfileConfigMalformedException` either.
Both are common, expected user mistakes that currently look like a crash rather than a validation
error. Additionally, no affected command's help text shows an example date/time value, making it
harder for a user to guess the accepted format before hitting the parse failure.

## What Changes

- Wrap date/time argument parsing for `--at`, `--since`, `--left-at`, and `--right-at` (in
  `RestoreCommand`, `BrowseCommand`, `DeletedCommand`, `DiffCommand`, `ShowCommand`) so an
  unparseable value reports a friendly, actionable error identifying the option and the invalid
  value, instead of an unhandled `FormatException` with a raw stack trace.
- Extend `YamlProfileConfigLoader.LoadProfiles` to catch a YAML syntax error raised while parsing
  `profiles.yml` and report it as a configuration error identifying the file and the parse
  problem, instead of letting the raw `YamlDotNet` exception propagate to the unhandled-exception
  catch-all.
- Add an example date/time value to the `--at`, `--since`, `--left-at`, and `--right-at` option
  descriptions shown by `-h`/`--help`, so a user can see the accepted format before providing one.

## Capabilities

### New Capabilities
(none)

### Modified Capabilities
- `cli-presentation`: adds a requirement that invalid date/time option input produces a friendly,
  actionable error (naming the option and the offending value) rather than falling through to the
  unclassified-exception catch-all, and a requirement that every command option accepting a
  date/time value documents an example value in its help text.
- `profile-config`: extends the existing "Malformed configuration file detection" requirement so
  a YAML syntax error encountered while parsing the file (not just a validly-parsed-but-wrong-shape
  document) is also reported as a configuration error rather than propagating unhandled.

## Impact

- `src/Vara.Cli/Commands/RestoreCommand.cs`, `BrowseCommand.cs`, `DeletedCommand.cs`,
  `DiffCommand.cs`, `ShowCommand.cs`: date/time option parsing and option descriptions.
- `src/Vara.Cli/Composition/ErrorReporting.cs`: recognizing the new friendly exception for invalid
  date/time input (exact exception type to be decided in design.md).
- `src/Vara.Infrastructure/Configuration/YamlProfileConfigLoader.cs`: wrapping the raw
  `YamlStream.Load` parse call.
- No changes to `src/Vara.Core/Configuration/ProfileConfigExceptions.cs`'s existing exception
  types are assumed yet; whether the YAML syntax error reuses `ProfileConfigMalformedException`
  or a new exception type is a design decision.
