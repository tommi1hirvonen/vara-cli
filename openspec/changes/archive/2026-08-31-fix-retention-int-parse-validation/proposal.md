## Why

`YamlProfileConfigLoader.GetOptionalInt` parses `keep_daily`, `keep_weekly`, `keep_monthly`, and `keep_yearly` with a raw `int.Parse`, unlike every other field in the loader (e.g. `recursive`), which validates the raw text and raises a friendly `ProfileValidationException` on bad input. A malformed retention count (e.g. `keep_daily: abc`) throws an unhandled `FormatException` that isn't in `ErrorReporting`'s known-exception list, so the user sees a raw .NET stack trace instead of the clean `Error: ...` message every other config mistake produces. A negative count (e.g. `keep_daily: -1`) parses successfully but is semantically meaningless for a retention tier count.

## What Changes

- Replace the raw `int.Parse` call in `GetOptionalInt` with validation that mirrors the `recursive` field's pattern: on an unparseable value, throw `ProfileValidationException` identifying the profile, the field name, and the offending value.
- Reject negative retention counts (`keep_daily`, `keep_weekly`, `keep_monthly`, `keep_yearly`) with the same friendly `ProfileValidationException`, since a negative tier count is not a meaningful retention policy.
- No change to valid-input behavior: well-formed non-negative integers continue to parse exactly as before.

## Capabilities

### New Capabilities

(none)

### Modified Capabilities

- `profile-config`: the "Retention policy configuration" requirement gains explicit validation behavior for malformed and negative retention tier counts, surfaced as a clear `ProfileValidationException` rather than an unhandled exception.

## Impact

- `src/Vara.Infrastructure/Configuration/YamlProfileConfigLoader.cs` (`GetOptionalInt`, `ParseRetention`)
- No changes to `ErrorReporting.cs` are needed, since the fix makes the thrown exception a `ProfileConfigException` subtype, which is already in its known-exception list.
- No public API or CLI surface changes; behavior only changes for previously-crashing malformed/negative retention input.
