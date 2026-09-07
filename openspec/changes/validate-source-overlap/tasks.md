## 1. Implement source-vs-source overlap validation

- [ ] 1.1 In `Profile`'s constructor, when `validateSourceOverlap` is true, add a pairwise check
      over `sources` using the existing `PathsOverlap` helper, throwing an `ArgumentException`
      that names both offending source paths when any pair overlaps
- [ ] 1.2 Word the exception message consistently with the existing target-vs-source overlap
      message (identify the profile-construction context and both offending paths)

## 2. Tests

- [ ] 2.1 Add `Vara.Core.Tests` cases for `Profile` construction covering: identical source paths,
      one source nested inside another, case/trailing-separator-only differences, and confirm
      each throws `ArgumentException` and verify all pass
- [ ] 2.2 Add a passing case with multiple non-overlapping sources and verify no exception is
      thrown
- [ ] 2.3 Add a case with three-or-more sources where only one non-adjacent pair overlaps, and
      verify it is still detected

## 3. Verification

- [ ] 3.1 Run the `Vara.Core.Tests` project and verify all tests pass, including the new overlap
      cases
- [ ] 3.2 Manually load a profile config with two overlapping sources via the CLI and verify a
      clear validation error is reported and no backup/scan action runs
