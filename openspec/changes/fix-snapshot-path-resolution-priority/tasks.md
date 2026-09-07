## 1. Reorder resolution rules

- [ ] 1.1 In `SnapshotPathResolver.TryResolve`, restructure the method so that when the
      cwd-resolved absolute path falls inside the mirror (`IsWithinMirror` returns `true`), the
      cwd-relative mirror candidate is checked against `hasHistory` before the literal input is
      checked
- [ ] 1.2 Ensure the literal check and the absolute-source-path conversion (rule 3) still both run
      as fallbacks in both branches (cwd-inside-mirror and cwd-outside-mirror), preserving
      first-match-wins semantics
- [ ] 1.3 Verify the method's existing XML doc comment (the `<list type="number">` describing
      resolution order) is updated to reflect the new conditional ordering

## 2. Tests

- [ ] 2.1 Add a `SnapshotPathResolver` test: `currentDirectory` inside the mirror, `hasHistory`
      matches both the cwd-relative candidate and the literal input (pointing at two different
      recorded paths), and verify the cwd-relative candidate wins
- [ ] 2.2 Add a regression test: `currentDirectory` outside the mirror, `hasHistory` matches the
      literal input, and verify the literal input still wins (unchanged from today)
- [ ] 2.3 Add a regression test: `currentDirectory` inside the mirror, cwd-relative candidate does
      not match, literal input does match, and verify the literal input is still used as a
      fallback
- [ ] 2.4 Add a regression test for the existing "absolute source path given directly" and
      "no interpretation matches" scenarios and verify they still pass unmodified
- [ ] 2.5 Run the full `Vara.Application.Tests` (or equivalent) suite and verify all tests pass

## 3. Verification

- [ ] 3.1 Manually reproduce the reported scenario: from inside a mirror subdirectory, run
      `vara history file.txt` where a same-named but unrelated `file.txt` is also recorded
      elsewhere in the profile, and confirm the file at the current location is now resolved
