## 1. Implement directory-level exclude short-circuiting

- [x] 1.1 In `DirectoryFileSystemScanner.Walk`, before recursing into a plain subdirectory, compute
      its relative path (already done for failure reporting) and check it against
      `IsExcluded`/`source.Excludes`; skip the recursive `Walk` call entirely when it matches, and
      verify by unit test that the directory's contents are never enumerated
- [x] 1.2 Ensure a reparse point (symlink/junction) is still reported exactly as today even when
      its own relative path would match an exclude entry - decide and document whether an
      excluded reparse point is still reported once (consistent with today's "always report, never
      follow" rule) or fully skipped, and verify with a unit test covering that decision
- [x] 1.3 Confirm `IsExcluded` needs no changes (only its call site moves) and verify existing
      `IsExcluded` unit tests still pass unmodified

## 2. Tests

- [x] 2.1 Add a scanner test with an excluded directory containing an unreadable nested file
      (simulate an `UnauthorizedAccessException` on enumeration) and verify no `ScanFailure` is
      recorded for it
- [x] 2.2 Add a scanner test asserting a mocked/instrumented filesystem's excluded directory is
      never enumerated (zero `Directory.EnumerateFileSystemEntries` calls under it)
- [x] 2.3 Add a regression test confirming non-excluded directories and file-level glob
      include/exclude filtering behave exactly as before
- [x] 2.4 Run the full `Vara.Infrastructure.Tests` (or equivalent scanner test project) suite and
      verify all tests pass

## 3. Verification

- [x] 3.1 Manually configure a profile with a large excluded directory (for example pointing
      `excludes` at a `node_modules` folder) and confirm via logs/timing that scanning completes
      without walking it
