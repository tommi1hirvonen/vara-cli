## Why

`DateTimeOptionParser.Parse` (used by `--at`, `--since`, `--left-at`, and `--right-at`) parses with `DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal)`. The invariant culture's date pattern is `MM/dd/yyyy`, so a value like `01/02/2025` parses silently and confidently as January 2nd - with no indication to a user from a dd/MM locale (or anyone who simply typed it the other way) that their intended date was misread. Today this is not merely a display quirk: because these options select which historical snapshot to restore, browse, or diff, a misread date can point at a different snapshot version than the user meant, silently.

## What Changes

- **BREAKING**: `DateTimeOptionParser.Parse` now requires an unambiguous date/time format (ISO 8601, e.g. `2025-01-02` or `2025-01-02T14:30:00`) and rejects slash-separated dates (`MM/dd/yyyy`, `dd/MM/yyyy`, or any other locale-dependent numeric-slash form) with the same friendly `InvalidDateTimeOptionException` already used for unparseable input, rather than silently guessing an interpretation.
- No change to `InvalidDateTimeOptionException`'s shape (still identifies the option name and the offending value) or to how a genuinely unparseable value (e.g. `yesterday`) is handled - it already produces this exception today.
- No change to the time component: a value may still include a time-of-day and/or an explicit UTC offset, as long as the date portion is unambiguous.

## Capabilities

### New Capabilities
(none)

### Modified Capabilities
- `cli-presentation`: the "Invalid date/time option input produces a friendly error" requirement is extended so an ambiguous (slash-separated, locale-dependent) date is treated as invalid input rather than being silently parsed under one specific, unstated interpretation.

## Impact

- `src/Vara.Cli/Composition/DateTimeOptionParser.cs`
- `tests/Vara.Cli.Tests/Composition/DateTimeOptionParserTests.cs`
- Any user or script currently passing a slash-separated date to `--at`/`--since`/`--left-at`/`--right-at` must switch to ISO 8601 (e.g. `2025-01-02`) - this is the intended breaking behavior, since those inputs were being silently misread rather than reliably handled
