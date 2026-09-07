## 1. Validate exclude/glob field shape

- [x] 1.1 Change `GetOptionalStringList` (or its caller) to accept the profile name and field name, and throw `ProfileValidationException` when the YAML node exists but is not a `YamlSequenceNode`, identifying the profile, the source, the field name, and that a list was expected
- [x] 1.2 Change the sequence-entry handling to throw `ProfileValidationException` for any entry that isn't a `YamlScalarNode`, identifying the profile, the source, the field name, and the offending entry, instead of silently filtering it out via `.OfType<YamlScalarNode>()`
- [x] 1.3 Apply the updated validation to all three call sites (`exclude`, `include_globs`, `exclude_globs`) in `ParseSource`

## 2. Test coverage

- [x] 2.1 Add a `YamlProfileConfigLoaderTests` case for each of `exclude`/`include_globs`/`exclude_globs` given as a scalar instead of a list, asserting `ProfileValidationException` is thrown identifying the field
- [x] 2.2 Add a `YamlProfileConfigLoaderTests` case for a list containing a nested mapping (and one for a nested sequence) under one of the three fields, asserting `ProfileValidationException` is thrown identifying the offending entry
- [x] 2.3 Confirm existing tests with well-formed `exclude`/`include_globs`/`exclude_globs` lists still pass unchanged, verifying no regression for valid configurations

## 3. Verify

- [x] 3.1 Run `cd src; dotnet test ..\tests\Vara.Infrastructure.Tests` and verify all `YamlProfileConfigLoaderTests` pass
- [x] 3.2 Confirm the updated `profile-config` delta spec's new scenarios are each covered by a test from Task 2
