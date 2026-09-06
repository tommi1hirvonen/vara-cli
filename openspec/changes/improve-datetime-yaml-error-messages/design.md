## Context

See proposal.md - Why. Six call sites across five commands (`RestoreCommand` x2, `BrowseCommand`,
`ShowCommand`, `DeletedCommand`, `DiffCommand` x2 for `--left-at`/`--right-at`) each call
`DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal)` directly
against the raw option string, with no try/catch. `ErrorReporting.Run` (`src/Vara.Cli/Composition/
ErrorReporting.cs`) is the single place that turns exceptions into user-facing messages, via
`TryGetFriendlyMessage`'s closed switch over known domain exception types; anything else falls to
a catch-all that prints `Unhandled exception: {TypeName}: {Message}` plus a dim stack trace.
Separately, `YamlProfileConfigLoader.LoadProfiles` (`src/Vara.Infrastructure/Configuration/
YamlProfileConfigLoader.cs`) calls `yamlStream.Load(reader)` with no try/catch; a YAML syntax
error there throws `YamlDotNet.Core.YamlException` (or a subtype such as
`SemanticErrorException`), before the loader's own shape checks - which already throw the
existing `ProfileConfigMalformedException` - ever run.

## Goals / Non-Goals

**Goals:**
- Every `--at`/`--since`/`--left-at`/`--right-at` value that fails to parse reports one consistent,
  friendly error naming the option and the offending value, through the existing
  `ErrorReporting.Run` mechanism (no new top-level error-handling path).
- A `profiles.yml` YAML syntax error reports through the same configuration-error path as the
  existing structural-malformation checks, rather than crashing.
- Every affected option's help text shows an example accepted value.

**Non-Goals:**
- Not changing the accepted date/time format or parsing behavior (`AssumeLocal`,
  `InvariantCulture`) for values that already parse successfully.
- Not adding a general-purpose "catch common BCL exceptions" mechanism to `ErrorReporting` -
  only this specific, purpose-built parse path is covered.
- Not changing `SqliteSnapshotRepository.ParseIso`, which parses the system's own
  round-trip-formatted timestamps rather than user-supplied CLI input.

## Decisions

### A single shared parsing helper, not per-command try/catch
Add `DateTimeOptionParser.Parse(string optionName, string value)` (static, in
`src/Vara.Cli/Composition/`, alongside `ErrorReporting` since both are CLI-layer error-translation
concerns) that calls the existing `DateTimeOffset.Parse(value, CultureInfo.InvariantCulture,
DateTimeStyles.AssumeLocal)` and, on `FormatException`, throws a new
`InvalidDateTimeOptionException(optionName, value)`. Each of the six call sites replaces its raw
`DateTimeOffset.Parse(...)` with `DateTimeOptionParser.Parse("--at", at)` (etc.), passing the
option's own flag name.
- *Alternative considered*: catch `FormatException` directly inside `TryGetFriendlyMessage`.
  Rejected - the message needs the option's flag name and offending value, which a bare
  `FormatException.Message` doesn't carry, and `FormatException` is a general BCL type that could
  legitimately originate from unrelated code in the future; catching it globally would risk
  silently swallowing an unrelated bug behind a misleading "friendly" message. A purpose-built
  exception thrown only from this one helper keeps the recognized-exception set precise.
- *Alternative considered*: six separate try/catch blocks at each call site. Rejected - the parse
  call and desired message shape are identical everywhere; a shared helper avoids duplicating
  that logic six times.

### New exception type lives with the CLI layer, not `Vara.Core`
`InvalidDateTimeOptionException(string optionName, string value)` is a new small exception (own
file or added to `ErrorReporting.cs`'s namespace) exposing `OptionName` and `Value`, with a message
like `Invalid value '{value}' for option '{optionName}': expected a date/time (for example,
'2025-01-15' or '2025-01-15 14:30').` It does not derive from `ProfileConfigException` (a
domain-layer base type for profile-configuration errors specifically) since this is a CLI
argument-parsing concern, not a profile-configuration concern. `ErrorReporting.TryGetFriendlyMessage`
adds it to its existing switch alongside the other recognized types.

### YAML syntax error reuses the existing `ProfileConfigMalformedException`
Wrap the `yamlStream.Load(reader)` call in `YamlProfileConfigLoader.LoadProfiles` in a
try/catch for `YamlDotNet.Core.YamlException` (the base type, so subtypes like
`SemanticErrorException` are also caught), and rethrow as
`ProfileConfigMalformedException(configPath, $"could not be parsed as YAML - {ex.Message}")`.
- *Alternative considered*: a new dedicated exception type for YAML syntax errors. Rejected -
  `ProfileConfigMalformedException`'s existing `(configPath, reason)` shape already fits a parse
  failure exactly as well as a shape failure, and the modified spec extends the same
  "Malformed configuration file detection" requirement rather than introducing a new one; a new
  type would be an unjustified addition recognized nowhere else.

### Help text: append an example inline to each option's existing `Description`
Append a short example, e.g. `(for example, '2025-01-15' or '2025-01-15 14:30')`, to the existing
`Description` string literal of each of the six option declarations (`--at` in `RestoreCommand`,
`BrowseCommand`, `ShowCommand`; `--since` in `DeletedCommand`; `--left-at`/`--right-at` in
`DiffCommand`). No shared constant is introduced given the small, fixed number of call sites and
the differing surrounding sentence in each description; keeping the example inline avoids an
indirection that would save nothing at this scale.

## Risks / Trade-offs

- [Risk] `YamlDotNet.Core.YamlException` might not be the common base for every syntax-error
  subtype the library can throw → Mitigation: verify during implementation (task) that
  `SemanticErrorException` and a plain scanner/parser error both derive from `YamlException`
  (already true in the version in use, confirmed by the archived
  `2026-09-01-polish-cli-visual-presentation` change's manual repro), and add a test covering at
  least one concrete malformed-YAML fixture.
- [Risk] Wrapping `DateTimeOffset.Parse` changes the exception type seen by any other code that
  might depend on catching `FormatException` from these call sites directly → Mitigation: grep
  confirms none of the five commands wrap these calls in their own try/catch today, so there is no
  existing catch to break.
