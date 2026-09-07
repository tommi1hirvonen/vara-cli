## 1. Restrict accepted date/time formats

- [ ] 1.1 Replace `DateTimeOptionParser.Parse`'s call to `DateTimeOffset.Parse` with `DateTimeOffset.TryParseExact` against an explicit ISO 8601 format list (date-only and date-plus-time, with and without an explicit offset), preserving `DateTimeStyles.AssumeLocal` for forms that omit an offset
- [ ] 1.2 On no format match, throw `InvalidDateTimeOptionException` exactly as for any other unparseable value today (no new exception type)

## 2. Test coverage

- [ ] 2.1 Add a test asserting a slash-separated date (e.g. `01/02/2025`) now throws `InvalidDateTimeOptionException` identifying the option and value
- [ ] 2.2 Add a test asserting an ISO 8601 date-only value (e.g. `2025-01-02`) still parses successfully, resolving the expected year/month/day
- [ ] 2.3 Add a test asserting an ISO 8601 date+time value with an explicit offset (e.g. `2025-01-02T14:30:00+02:00`) still parses successfully, resolving the expected offset
- [ ] 2.4 Add a test asserting an ISO 8601 date+time value without an offset still resolves under `AssumeLocal`, matching current behavior
- [ ] 2.5 Confirm the existing `yesterday` (unparseable) test still throws `InvalidDateTimeOptionException`

## 3. Verify

- [ ] 3.1 Run `cd src; dotnet test ..\tests\Vara.Cli.Tests` and verify all `DateTimeOptionParserTests` pass
- [ ] 3.2 Confirm the updated `cli-presentation` delta spec's new "Ambiguous slash-separated date" scenario is covered by a test from Task 2
- [ ] 3.3 Update the `README.md` command reference if any example currently shows a non-ISO date format for `--at`/`--since`/`--left-at`/`--right-at`
