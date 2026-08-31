## 1. Fix retention field parsing

- [x] 1.1 In `src/Vara.Infrastructure/Configuration/YamlProfileConfigLoader.cs`, change `GetOptionalInt` to validate the raw text with `int.TryParse` instead of calling `int.Parse` directly, mirroring the pattern already used for the `recursive` field.
- [x] 1.2 On a parse failure, throw `ProfileValidationException` identifying the profile name, the field name (`keep_daily`/`keep_weekly`/`keep_monthly`/`keep_yearly`), and the offending value - update `GetOptionalInt` and/or `ParseRetention` in the same file so the field name is available at the throw site, and verify by inspecting the call sites in `ParseRetention`.
- [x] 1.3 Reject negative parsed values with the same `ProfileValidationException` pattern (identifying profile, field name, and value), and verify zero and positive values continue to parse unchanged.

## 2. Tests

- [x] 2.1 In `tests/Vara.Infrastructure.Tests/Configuration/YamlProfileConfigLoaderTests.cs`, add a test asserting that a non-numeric `keep_daily` (or any of the four retention fields) throws `ProfileValidationException` with a message naming the profile, field, and value, and verify the test fails against the old `int.Parse` behavior conceptually (i.e. it exercises the previously-unhandled `FormatException` path) and passes after the fix.
- [x] 2.2 Add a test asserting that a negative retention value (e.g. `keep_daily: -1`) throws `ProfileValidationException`, and verify it passes.
- [x] 2.3 Add or confirm an existing test that a valid retention policy (all four fields zero or positive integers, and the case where the `retention` block is omitted) still loads successfully with the expected `RetentionPolicy` values, and verify it passes.
- [x] 2.4 Run `dotnet test tests/Vara.Infrastructure.Tests` and verify all tests pass.

## 3. Verification

- [x] 3.1 Run the full test suite (`dotnet test`) and verify no regressions in other test projects.
- [x] 3.2 Manually confirm (via a scratch `profiles.yml` with `keep_daily: abc`) that running a Vara command now prints a clean `Error: Profile '<name>' is invalid: ...` line instead of a raw unhandled-exception stack trace.
