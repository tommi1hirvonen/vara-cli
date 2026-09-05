## 1. Overlap detection in `Profile`

- [x] 1.1 Add a private static path-normalization/containment helper to `Profile` (trim trailing
      `\`/`/`, case-insensitive compare, equal-or-prefix-with-separator-boundary check), and a
      `validateSourceOverlap = true` optional constructor parameter that, when `true`, checks the
      target root against every source's path in both directions and throws `ArgumentException`
      identifying the offending source path and target root on overlap
- [x] 1.2 Update `ProfileResolver.TryResolveFromWorkingDirectory` to pass
      `validateSourceOverlap: false` when constructing its synthetic placeholder profile, with a
      comment referencing why (placeholder source intentionally equals target root)
- [x] 1.3 Add/extend `ProfileTests` (tests/Vara.Core.Tests/Configuration/ProfileTests.cs) covering:
      target equals source, target nested in source, source nested in target, case-insensitive and
      trailing-separator overlap, non-overlapping paths accepted, and
      `validateSourceOverlap: false` bypassing the check - verify via `dotnet test
      --filter FullyQualifiedName~Vara.Core.Tests.Configuration.ProfileTests`

## 2. Surfacing the error from YAML loading

- [x] 2.1 In `YamlProfileConfigLoader.ParseProfile`, catch the `ArgumentException` thrown by the
      new overlap check when constructing a `Profile` and rethrow it as a
      `ProfileValidationException(name, reason)`, consistent with the existing handling of other
      structural validation failures in that method
- [x] 2.2 Add/extend `YamlProfileConfigLoaderTests`
      (tests/Vara.Infrastructure.Tests/Configuration/YamlProfileConfigLoaderTests.cs) covering a
      profile whose `target` equals or contains/is contained by one of its `sources[].path`
      entries, asserting a `ProfileValidationException` naming the profile and the offending
      paths - verify via `dotnet test
      --filter FullyQualifiedName~Vara.Infrastructure.Tests.Configuration.YamlProfileConfigLoaderTests`

## 3. Regression check

- [x] 3.1 Confirm `ProfileResolverTests` (tests/Vara.Application.Tests/Profiles/ProfileResolverTests.cs)
      still pass unmodified, proving the synthetic working-directory profile construction path is
      unaffected - verify via `dotnet test
      --filter FullyQualifiedName~Vara.Application.Tests.Profiles.ProfileResolverTests`
- [x] 3.2 Run the full solution test suite and verify all tests pass: `dotnet build Vara.slnx -c
      Debug --nologo -v quiet` followed by `dotnet test Vara.slnx --nologo` from the repository
      root
