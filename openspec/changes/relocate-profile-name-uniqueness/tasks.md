## 1. Move the helper

- [ ] 1.1 Move `ProfileNameUniqueness` from `src/Vara.Application/Profiles/ProfileNameUniqueness.cs`
      to `src/Vara.Core/Configuration/ProfileNameUniqueness.cs` under the
      `Vara.Core.Configuration` namespace, keeping the class static and the
      `ConflictsWithAnotherProfile(IEnumerable<string>, string, string?)` signature, XML docs, and
      comparison semantics byte-for-byte identical, and verify the file no longer exists under
      `Vara.Application`
- [ ] 1.2 Move `tests/Vara.Application.Tests/Profiles/ProfileNameUniquenessTests.cs` to
      `tests/Vara.Core.Tests/Configuration/ProfileNameUniquenessTests.cs`, changing only its
      namespace and `using` directives, and verify all five existing assertions pass unchanged

## 2. Update the call sites

- [ ] 2.1 Update `YamlProfileConfigLoader` to reference the helper from `Vara.Core.Configuration`
      (drop the `using Vara.Application.Profiles;` import) and verify the existing
      `DuplicateProfileNameException` loader tests pass unchanged
- [ ] 2.2 Update `ProfileDraft` to reference the helper from `Vara.Core.Configuration` and verify
      the existing `ProfileDraftTests` uniqueness tests pass unchanged

## 3. Restore the layering

- [ ] 3.1 Remove the `<ProjectReference Include="..\Vara.Application\Vara.Application.csproj" />`
      entry from `src/Vara.Infrastructure/Vara.Infrastructure.csproj`, and verify the solution
      still builds - a build failure here would mean some other infrastructure code had started
      depending on `Vara.Application`
- [ ] 3.2 Verify `Vara.Infrastructure`'s only project reference is `Vara.Core`, and that no file
      under `src/Vara.Infrastructure` still contains `using Vara.Application` (grep to confirm)

## 4. Verification

- [ ] 4.1 Build with `dotnet build Vara.slnx -c Debug --nologo -v quiet` and confirm it succeeds
      with `PublishAot=true` still set on `Vara.Cli`
- [ ] 4.2 Run `dotnet test` and confirm the whole suite passes with no assertion changed by this
      refactor - since this change alters no behavior, any failing test indicates the move was not
      behavior-preserving
