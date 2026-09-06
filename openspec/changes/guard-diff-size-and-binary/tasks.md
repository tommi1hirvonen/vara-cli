## 1. Core exceptions

- [ ] 1.1 Add `DiffContentTooLargeException` and `DiffBinaryContentException` to
  `src/Vara.Core/Snapshots/SnapshotExceptions.cs`, following the existing style (relative path,
  and for the too-large case the offending side(s)' size(s), as constructor args/properties;
  message text naming only the side(s) actually implicated). Verify the project still builds
  (`dotnet build`).

## 2. Diff guard implementation

- [ ] 2.1 In `src/Vara.Application/History/SnapshotHistoryService.cs`, add a size check in
  `OpenVersionsForDiff` that compares both already-resolved `FileVersionRecord.Size` values
  against a 10 MB constant before either `contentStore.OpenRead` call, throwing
  `DiffContentTooLargeException` naming every side that exceeds the limit. Verify with a unit
  test in `tests/Vara.Application.Tests/History/SnapshotHistoryServiceTests.cs` covering: only
  the left side oversized, only the right side oversized, and both sides oversized (asserting
  the exception names the correct side(s) in each case).
- [ ] 2.2 Add a binary-content check in `OpenVersionsForDiff`, run only after both sizes pass the
  guard in 2.1: open both streams, read a bounded initial sample (first 8000 bytes or the whole
  stream if shorter) from each, and detect binary content via a NUL byte in the sample; reset
  each stream back to position 0 (or reopen) before returning it so the caller's subsequent full
  read is unaffected. Throw `DiffBinaryContentException` naming every side detected as binary.
  Verify with unit tests in `tests/Vara.Application.Tests/History/SnapshotHistoryServiceTests.cs`
  covering: only the left side binary, only the right side binary, both sides binary, and a
  binary sample beyond a NUL byte still returning readable content afterward (proving the reset
  actually rewinds).
- [ ] 2.3 Verify size and binary guards compose in the stated order: a case with one side
  oversized and the other binary reports only the too-large error (binary sampling never runs
  for a comparison already refused on size), asserted via a unit test in
  `tests/Vara.Application.Tests/History/SnapshotHistoryServiceTests.cs`.

## 3. CLI error reporting

- [ ] 3.1 Add `DiffContentTooLargeException` and `DiffBinaryContentException` to the recognized
  exception list in `src/Vara.Cli/Composition/ErrorReporting.cs`'s `TryGetFriendlyMessage`, so
  `diff` reports each as a clean `Error: ...` line instead of an unhandled-exception dump.
  Verify with an existing-style CLI test asserting the friendly message and exit code 1 for both
  exception types (add to whatever test file already covers `ErrorReporting`/`DiffCommand`
  error paths, or create one alongside them if none exists).

## 4. End-to-end verification

- [ ] 4.1 Add an integration-level test (alongside
  `tests/Vara.IntegrationTests/History/SnapshotHistoryServiceRealPortsTests.cs`'s existing
  `OpenVersionsForDiff` coverage) that backs up a profile containing one oversized file version
  and one binary file version, then invokes `diff` end-to-end and asserts the clear refusal
  message for each, exercising the real content store rather than a fake.
- [ ] 4.2 Run the full test suite (`dotnet test`) and confirm all tests pass, including the new
  ones and the existing `OpenVersionsForDiff`/`diff` coverage (which must keep passing unchanged
  for ordinary, small, text-file diffs).
