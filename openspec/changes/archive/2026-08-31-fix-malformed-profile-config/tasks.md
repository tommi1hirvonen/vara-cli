## 1. Exception type

- [x] 1.1 Add `ProfileConfigMalformedException(string configPath, string reason)` to `src/Vara.Core/Configuration/ProfileConfigExceptions.cs`, following the existing `ProfileConfigException` subclass pattern (public `ConfigPath`/`Reason` properties, message built from both). Verify the solution builds.

## 2. Loader changes

- [x] 2.1 In `YamlProfileConfigLoader.LoadProfiles`, change the "root node is not a `YamlMappingNode`" branch to throw `ProfileConfigMalformedException(configPath, "the document root must be a mapping")` instead of returning `[]`.
- [x] 2.2 Split the current combined "`profiles` missing or not a sequence" check into two distinct branches: if the `profiles` key is absent, keep returning `[]` (unchanged, lenient); if `profiles` is present but is not a `YamlSequenceNode`, throw `ProfileConfigMalformedException(configPath, "'profiles' must be a list")`.
- [x] 2.3 Confirm the zero-document (empty file) branch is untouched and still returns `[]`.

## 3. Tests

- [x] 3.1 Add a test asserting that a config file whose YAML root is a sequence (or scalar) throws `ProfileConfigMalformedException`, and that `ConfigPath` matches the file path.
- [x] 3.2 Add a test asserting that a config file with `profiles:` set to a mapping (or scalar) instead of a list throws `ProfileConfigMalformedException`.
- [x] 3.3 Add/confirm a regression test that an empty (or comments-only) config file still returns an empty profile list without throwing.
- [x] 3.4 Add/confirm a regression test that a config file whose root mapping has no `profiles` key still returns an empty profile list without throwing.
- [x] 3.5 Run `dotnet test tests/Vara.Infrastructure.Tests` and verify all tests pass, including the four above.

## 4. Full verification

- [x] 4.1 Run the full solution test suite (`dotnet test`) and confirm no regressions in `Vara.Core.Tests` or `Vara.Application.Tests` (in particular `ProfileResolverTests`, which exercises `UnknownProfileException` via a fake loader and is unaffected by this change).
