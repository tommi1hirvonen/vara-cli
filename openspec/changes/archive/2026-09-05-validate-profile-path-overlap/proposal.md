## Why

Nothing today prevents a profile's target root from overlapping one of its own source paths. A
profile with `target: C:\Users\me\backup` and a source of `C:\Users\me\` causes each backup run to
mirror its own previous mirror into itself, growing the target unboundedly on every run. This is
cheap to catch at config load / profile construction time, before any scan or transfer runs.

## What Changes

- The `Profile` constructor rejects a target root that is the same as, an ancestor of, or a
  descendant of any of its own sources' paths (containment checked in both directions).
- `YamlProfileConfigLoader` surfaces this as a profile validation error (identifying the profile
  name, the offending source path, and the target root) instead of letting an unhandled exception
  propagate, consistent with how other structural profile errors are reported today.
- Path comparison is case-insensitive and normalizes trailing separators, matching the existing
  conventions used for exclude-path and mirror-path comparisons elsewhere in the codebase.
- Cross-profile overlap (one profile's target overlapping a different profile's source) is
  explicitly out of scope for this change.

## Capabilities

### New Capabilities

(none)

### Modified Capabilities

- `profile-config`: adds a requirement that a profile's target root must not overlap (be equal to,
  contain, or be contained by) any of its own source paths.

## Impact

- `src/Vara.Core/Configuration/Profile.cs`: constructor gains the overlap check, throwing an
  `ArgumentException` (mirroring existing constructor validation style).
- `src/Vara.Infrastructure/Configuration/YamlProfileConfigLoader.cs`: catches/translates the
  overlap condition into a `ProfileValidationException` at parse time so the whole config file
  fails fast with a clear message, consistent with other `ParseProfile` validations.
- No changes to `DirectoryFileSystemScanner` or backup execution - the fix is entirely at
  config-load/construction time, before any scan happens.
