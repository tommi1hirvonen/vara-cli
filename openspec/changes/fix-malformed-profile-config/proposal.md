## Why

`YamlProfileConfigLoader.LoadProfiles` silently returns an empty profile list when the YAML document's root is not a mapping, or when `profiles` is present but not a sequence. Because these malformed-file cases collapse into "zero profiles" the same as a legitimately empty configuration, `ProfileResolver` then reports a misleading `UnknownProfileException` ("no profile named 'x'") instead of telling the user their configuration file itself is broken.

## What Changes

- Introduce a new `ProfileConfigMalformedException(configPath, reason)` under `Vara.Core.Configuration`.
- `YamlProfileConfigLoader.LoadProfiles` throws `ProfileConfigMalformedException` when:
  - the YAML document's root node is not a mapping, or
  - a `profiles` key is present but is not a sequence.
- Two cases remain intentionally lenient and continue to produce an empty profile list (no behavior change):
  - the file has zero YAML documents (empty/comment-only file), and
  - the root mapping has no `profiles` key at all.

## Capabilities

### New Capabilities
(none)

### Modified Capabilities
- `profile-config`: adds a requirement that the loader must distinguish a structurally malformed configuration file (root not a mapping, or `profiles` present but not a list) from a configuration file that legitimately defines zero profiles, and must report the former as a configuration error rather than deferring to a later "unknown profile" error.

## Impact

- `src/Vara.Core/Configuration/ProfileConfigExceptions.cs`: new `ProfileConfigMalformedException` type.
- `src/Vara.Infrastructure/Configuration/YamlProfileConfigLoader.cs`: two silent `return [];` paths replaced with thrown exceptions; two others (empty file, missing `profiles` key) left unchanged.
- `tests/Vara.Infrastructure.Tests/Configuration/YamlProfileConfigLoaderTests.cs`: new test cases for both malformed-shape cases and both still-lenient cases.
- No changes to command-line surface, `IProfileConfigLoader` interface, or other consumers of `ProfileResolver`.
