## 1. Directory-resolution helper

- [ ] 1.1 In `DirectoryArgumentResolver`, replace the unconditional "empty prefix is always
      tracked" special case with a helper that: when the raw input is `.`/blank AND the cwd does
      not resolve inside the mirror, tries the absolute-source-path-mapped candidate against the
      real `hasHistory` predicate first, and only falls back to the literal mirror root if that
      candidate has no recorded history - returning whether a fallback occurred. Verify with a
      new unit test in `DirectoryArgumentResolverTests` asserting a `.` input from a cwd outside
      the mirror resolves to the source-mapped mirror-relative path when that location is
      tracked.
- [ ] 1.2 Update `The_mirror_root_itself_always_resolves` (or replace it) to reflect the new
      behavior: `.` still resolves to the mirror root when the source-mapped candidate has no
      recorded history, and the resolver now also signals that this was a fallback. Verify the
      updated test passes and still documents the "root is a safe default" guarantee.
- [ ] 1.3 Add a unit test covering the case where the cwd (outside the mirror) source-maps to a
      location that itself has no recorded history, asserting the resolver falls back to root and
      reports the fallback flag. Verify via `dotnet test` targeting
      `Vara.Cli.Tests.Commands.DirectoryArgumentResolverTests`.
- [ ] 1.4 Add/confirm a unit test that a non-root, non-blank directory argument (e.g. `subdir`)
      from a cwd outside the mirror is unaffected by this change (still resolves via the existing
      literal-then-source-mapped order). Verify the test passes.

## 2. `browse` command wiring

- [ ] 2.1 Update `BrowseCommand` to consume the resolver's fallback signal and print a brief,
      clear message (via the command's existing output conventions) when the mirror root is shown
      as a fallback rather than the requested current directory. Verify by running
      `vara browse .` (or a CLI test) from a source-tree cwd with no tracked history there and
      observing the message alongside the mirror-root listing.
- [ ] 2.2 Verify `vara browse .` (or omitting the argument) from a source-tree cwd that does map
      to tracked history now lists that source-mapped directory instead of the mirror root, with
      no fallback message printed.

## 3. `restore --recursive` wiring

- [ ] 3.1 Extract/align `RestoreCommand.IsDirectoryTracked` and `RunRecursiveRestore`'s directory
      resolution to call the same shared `.`/blank-with-fallback helper from
      `DirectoryArgumentResolver` used by `browse`, instead of duplicating equivalent logic.
      Verify existing `RestoreCommandTests` still pass.
- [ ] 3.2 Print the same kind of brief fallback message (using `RestoreCommand`'s existing
      `OutcomeStyle`/`StandardError` conventions) when `restore --recursive .` falls back to the
      mirror root. Verify with a new/updated test in `RestoreCommandTests` asserting the message
      appears when the cwd's source-mapped location has no recorded history.
- [ ] 3.3 Add a test asserting `restore --recursive .` from a source-tree cwd that does map to
      tracked history restores that source-mapped directory, not the mirror root. Verify the test
      passes.

## 4. Spec and regression verification

- [ ] 4.1 Run the full existing test suite for the affected projects
      (`dotnet test tests/Vara.Cli.Tests`) and confirm no regressions beyond the intentionally
      updated tests from section 1.
- [ ] 4.2 Manually verify (or add an integration test) that `browse`/`restore --recursive` with a
      cwd inside the profile's mirror are unchanged: `.` still resolves via the cwd-relative
      mirror candidate, with no fallback message.
