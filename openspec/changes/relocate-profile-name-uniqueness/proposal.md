## Why

Sharing the "no two profiles share a name" comparison between the configuration loader and the
interactive editor was the right call, but the shared helper was placed in `Vara.Application` and
the loader lives in `Vara.Infrastructure` - so `Vara.Infrastructure` now carries a project
reference to `Vara.Application` purely to reach a three-line string comparison. That inverts the
project's dependency direction (infrastructure implements abstractions defined below it; it should
not depend on the application layer above it) and, because it is a compile-time reference, it
silently permits any future infrastructure code to reach into application types. The rule itself is
a configuration invariant about the domain, so it belongs with the other configuration invariants
in `Vara.Core`, which both the loader and the editor already reference.

## What Changes

- The cross-profile duplicate-name comparison moves to `Vara.Core` alongside the other profile
  configuration invariants, keeping its current signature and semantics (case-insensitive
  comparison, one excluded "profile being replaced" entry) unchanged.
- The configuration loader and the interactive editor's live validation both call it from its new
  location; neither one's behavior changes.
- The `Vara.Infrastructure` → `Vara.Application` project reference is removed, restoring the
  intended layering.
- **No behavior change**: duplicate-name detection at load time and in the editor produces exactly
  the same outcomes and messages as before, so no requirement changes and this change declares
  `skip_specs: true`.

## Capabilities

### New Capabilities
(none)

### Modified Capabilities
(none - this is a pure internal relocation with no spec-level behavior change; the change declares
`skip_specs: true` in its `.openspec.yaml`)

## Impact

- Moves `Vara.Application.Profiles.ProfileNameUniqueness` to `Vara.Core.Configuration` (**BREAKING**
  only for anything referencing that type by namespace; all references are inside this repository).
- `Vara.Infrastructure.Configuration.YamlProfileConfigLoader` and
  `Vara.Application.Profiles.ProfileDraft`: import updates only.
- `Vara.Infrastructure.csproj`: drops the `Vara.Application` project reference.
- Test impact: the existing uniqueness unit tests move to the test project covering `Vara.Core`;
  loader and editor tests are unchanged apart from imports.
