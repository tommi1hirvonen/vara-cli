## Why

`show` and `diff` resolve a version's content through `SnapshotHistoryService.ShowVersion`
and `OpenVersionsForDiff`, but neither guards against the resolved version being a
symlink/junction entry (`ContentHash == null`) the way `RestoreVersion`/`RestoreAsOf`
already do via `GuardNotLinked`. Requesting `show` or `diff` on such a version instead
throws `MissingContentHashException` from deep inside the content store - an exception
`ErrorReporting` doesn't classify, so it surfaces as an unstyled "Unhandled exception"
message with a raw stack trace instead of a clear, actionable error. Separately,
`OpenVersionsForDiff` opens both sides' content streams before entering its `try` block,
so if the second `OpenRead` call throws (for example because that side is unavailable),
the first stream that already opened successfully is never disposed.

## What Changes

- Add a linked-entry guard to `SnapshotHistoryService.ShowVersion` and
  `OpenVersionsForDiff`, executed immediately after each side's version is resolved and
  before any content stream is opened.
- Introduce a new exception (distinct from `RestoreVersion`'s `RestoreLinkedEntryException`,
  whose message is restore-specific) carrying a message appropriate for `show`/`diff`
  callers, and register it in `ErrorReporting`'s classified exception list so it renders
  as a clean, friendly error rather than an unhandled-exception dump.
- Move both `OpenRead` calls in `OpenVersionsForDiff` inside the existing `try` block (or
  otherwise ensure a stream opened for one side is always disposed if opening the other
  side fails), closing the stream leak.
- Add test coverage for `show`/`diff` against a linked (symlink/junction) version,
  mirroring the existing coverage `RestoreVersion`/`RestoreAsOf` already have for the
  same condition.

## Capabilities

### New Capabilities
(none)

### Modified Capabilities
- `snapshot-history`: the "Show a version's content directly" and "Diff two versions of
  a file" requirements gain an explicit, clearly-reported error case for a resolved
  version that is a symlink/junction entry with no stored content.

## Impact

- `src/Vara.Application/History/SnapshotHistoryService.cs` (`ShowVersion`,
  `OpenVersionsForDiff`, new guard usage)
- `src/Vara.Core/Snapshots/SnapshotExceptions.cs` (new exception type)
- `src/Vara.Cli/Composition/ErrorReporting.cs` (classify the new exception)
- Existing unit tests covering `ShowVersion`/`OpenVersionsForDiff` and `RestoreVersion`'s
  linked-entry handling, for reference/parity
