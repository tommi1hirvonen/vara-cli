## 1. Exception and error reporting

- [x] 1.1 Add `ShowOrDiffLinkedEntryException` to `Vara.Core/Snapshots/SnapshotExceptions.cs`, carrying the relative path and per-side (left/right, nullable) linked indicators so a single type serves both `show` and `diff`, and verify it builds
- [x] 1.2 Register `ShowOrDiffLinkedEntryException` in `ErrorReporting.TryGetFriendlyMessage`'s switch in `src/Vara.Cli/Composition/ErrorReporting.cs`, and verify a unit test asserts it renders as a friendly `Error: ...` message rather than falling through to the unhandled-exception path

## 2. Guard reuse in SnapshotHistoryService

- [x] 2.1 Extend `GuardNotLinked` in `src/Vara.Application/History/SnapshotHistoryService.cs` (or add a small overload/parameter) so it can throw either `RestoreLinkedEntryException` (existing restore callers) or the new `ShowOrDiffLinkedEntryException` (show/diff callers), and verify existing restore tests still pass unchanged
- [x] 2.2 Call the guard in `ShowVersion` immediately after `ResolveVersion` and before `contentStore.OpenRead`, and verify a new unit test shows requesting a linked version reports the new friendly error instead of `MissingContentHashException`
- [x] 2.3 Call the guard in `OpenVersionsForDiff` immediately after each side's `ResolveVersion` and before either `OpenRead` call, and verify a new unit test diffing a linked version on either side reports the new friendly error naming the affected side(s)

## 3. Stream leak fix

- [x] 3.1 Move both `contentStore.OpenRead(...)` calls in `OpenVersionsForDiff` inside the existing `try` block (after the linked-entry guards from task 2.3, so no stream is opened before both sides are known to have content), and verify the existing binary-check `catch` still disposes both streams on failure
- [x] 3.2 Add a unit test that opens one side's stream successfully then fails on the other (oversized/binary/linked), asserting the first stream is disposed rather than left open (e.g. asserting the underlying file is no longer held open, or via a test double that tracks `Dispose` calls)

## 4. Regression coverage

- [x] 4.1 Run the existing `SnapshotHistoryService`/`ShowCommand`/`DiffCommand`/`RestoreCommand` test suites and verify all pass with no behavior change for non-linked versions
