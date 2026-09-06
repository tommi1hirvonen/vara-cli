## Why

`diff` fully buffers both resolved versions into in-memory strings (`StreamReader.ReadToEnd`)
before diffing, with no check on either side's size or textual nature first. A large file blows
up memory (and, if it somehow succeeds, spams the console with a huge line-by-line diff); a binary
file gets "diffed" as if it were text, producing meaningless byte-garbage output. Both sides'
sizes are already known cheaply from the recorded manifest before any content is read, and a
binary check needs only a small bounded read - so both problems are fixable without materially
changing how `diff` resolves or opens versions.

## What Changes

- `diff` refuses to compare a version whose recorded size exceeds a fixed threshold (10 MB),
  reporting a clear error naming the offending side and its size, instead of reading it at all.
- `diff` refuses to compare a version whose content is detected as binary (a NUL byte within a
  bounded initial sample, the same heuristic used by common VCS tools), reporting a clear error
  naming the offending side, instead of producing a meaningless byte-level "diff".
- Both checks run before either version's full content is read into memory: the size check uses
  already-known manifest metadata, and the binary check reads only a small bounded sample per
  side.
- Both sides are checked before failing, so a two-sided problem (e.g. both versions oversized) is
  still reported clearly rather than only ever naming the left side.

## Capabilities

### New Capabilities

(none)

### Modified Capabilities

- `snapshot-history`: `diff` gains a size guard and a binary-content guard, each refusing the
  operation with a clear error before reading either version's full content.

## Impact

- `src/Vara.Application/History/SnapshotHistoryService.cs`: `OpenVersionsForDiff` gains the
  size and binary checks (size from the already-resolved `FileVersionRecord`/`CurrentFileState`,
  binary via a bounded sample read from each opened stream) before returning the streams to the
  caller.
- `src/Vara.Core/Snapshots/SnapshotExceptions.cs`: two new exceptions for the refused cases,
  following the existing `NoMatchingVersionException`/`NoHistoryForPathException` style.
- `src/Vara.Cli/Composition/ErrorReporting.cs`: recognizes the two new exceptions so `diff`
  reports them as a clean CLI error instead of an unhandled-exception stack trace.
- `src/Vara.Cli/Commands/DiffCommand.cs`: unaffected beyond receiving/reporting the new errors -
  it still just opens both streams and reads them once past the guards.
- No change to `show`'s raw-bytes-to-stdout behavior for binary content, nor to `restore`'s
  behavior for large/binary files - both remain intentionally content-agnostic; this change is
  scoped to `diff` only.
