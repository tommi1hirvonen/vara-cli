## Why

`YamlProfileConfigLoader.GetOptionalStringList` (used to parse a source's `exclude`, `include_globs`, and `exclude_globs` fields) silently returns `null` when the YAML node under one of those keys isn't a sequence at all (for example, a typo'd `exclude: "*.tmp"` written as a scalar instead of a list), and silently drops any list entry that isn't a plain scalar (via `.OfType<YamlScalarNode>()`). Both cases are treated identically to the field simply being absent - no excludes/globs applied - with no error and no indication to the user that their configuration was misread. This contradicts the existing validation philosophy already established for every other structurally-malformed part of a profile (missing fields, non-mapping entries, invalid retention/concurrency values), all of which report a validation error identifying the profile and the problem rather than silently substituting a default.

## What Changes

- `exclude`, `include_globs`, and `exclude_globs` fields whose YAML node exists but is not a sequence now raise `ProfileValidationException` identifying the profile, the field name, and the problem, instead of being silently treated as absent.
- A sequence entry under any of those three fields that is not a plain scalar (for example, a nested mapping or another sequence) now raises `ProfileValidationException` identifying the profile, the field name, and the offending entry, instead of being silently dropped from the list.
- Not BREAKING for any currently-valid configuration file: a well-formed list of string entries under these fields continues to load exactly as before. Only configurations that were previously silently misread (and therefore already not behaving as the user intended) are affected, and they now fail loudly at startup instead.

## Capabilities

### New Capabilities
(none)

### Modified Capabilities
- `profile-config`: the "Profile structure validation" requirement is extended so a source's `exclude`/`include_globs`/`exclude_globs` fields are validated the same way other structurally-malformed profile fields already are, rather than being silently ignored when malformed.

## Impact

- `src/Vara.Infrastructure/Configuration/YamlProfileConfigLoader.cs` (`GetOptionalStringList`, `ParseSource`)
- `tests/` covering `YamlProfileConfigLoader` (add malformed-node and malformed-entry cases)
