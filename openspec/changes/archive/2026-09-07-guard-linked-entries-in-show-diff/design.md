## Context

`SnapshotHistoryService` already has a private `GuardNotLinked(relativePath, match)` helper,
called by `RestoreVersion`/`RestoreAsOf` right after `ResolveVersion`, that throws
`RestoreLinkedEntryException` when a resolved version's `ChangeKind` is `Linked` (no stored
content). `ShowVersion` and `OpenVersionsForDiff` resolve versions the same way but never call
it, so a linked version reaches `contentStore.OpenRead(match.ContentHash)` with a `null` hash,
which throws `MissingContentHashException` - unclassified by `ErrorReporting`, so it renders as
an unhandled-exception dump. See proposal.md - Why.

## Goals / Non-Goals

**Goals:**
- `show` and `diff` report a clear, friendly error for a linked version, matching the quality of
  restore's existing handling.
- Fix the `OpenVersionsForDiff` stream leak as part of the same change, since it sits in the
  exact code path being touched.
- Reuse the existing `ResolveVersion` → guard → act structure already established by
  `RestoreVersion`/`RestoreAsOf`, rather than inventing a new pattern.

**Non-Goals:**
- No change to how `RestoreVersion`/`RestoreAsOf` behave - they already guard correctly.
- No change to the binary/oversized-content refusal logic in `ShowVersion`/`OpenVersionsForDiff`.

## Decisions

**A dedicated exception for show/diff, not a reused `RestoreLinkedEntryException`.**
Per the user's direction, a linked entry on `show`/`diff` gets its own message rather than
reusing restore's wording (which reads oddly outside a restore context, e.g. "...has no content
to restore" or `RestoreDestinationInMirrorException`-adjacent whichever gets confusing when
nothing is being restored). Introduce `ShowOrDiffLinkedEntryException`
in `Vara.Core/Snapshots/SnapshotExceptions.cs`, alongside the existing restore/show/diff
exceptions, with a message along the lines of "'<path>' has no content to show/diff at this
version." A single exception type is shared by both `show` and `diff`; `diff`'s catch path (or
the exception itself) is responsible for naming which side(s) are affected, mirroring how
`DiffContentTooLargeException`/`DiffBinaryContentException` already report per-side problems.
Register the new exception in `ErrorReporting.TryGetFriendlyMessage`'s switch alongside the
other snapshot exceptions.

**Guard placement:** call `GuardNotLinked` immediately after `ResolveVersion` in `ShowVersion`,
and immediately after each of the two `ResolveVersion` calls in `OpenVersionsForDiff` - before
either `OpenRead` call - so a linked entry is caught before any stream is opened, exactly
mirroring the existing `RestoreVersion` structure (`ResolveVersion` → `GuardNotLinked` → act).
`GuardNotLinked` itself is reused as-is (still private, same signature) but a `show`/`diff`
caller needs it to throw the new exception type instead of `RestoreLinkedEntryException`. Since
`GuardNotLinked` is shared by three call sites with two different exception types depending on
caller, give it a parameter (or a small enum/delegate) for which exception to throw, rather than
forking it into near-duplicate private methods.

**Stream leak fix:** move both `contentStore.OpenRead(...)` calls inside the existing `try` block
in `OpenVersionsForDiff` (immediately followed by the existing binary-check `try`/`catch` that
already disposes both streams on failure), so any exception after the first `OpenRead` -
including one thrown by a guard evaluated between the two resolves, or a failure opening the
second stream - is guaranteed to dispose whichever stream(s) already opened. Concretely: resolve
both versions and guard both for linked entries first (no streams open yet, nothing to leak),
then open both streams inside the `try`, then binary-check as today.

## Risks / Trade-offs

[Shared exception type across show and diff, but each needs a different rendered message
depending on which side(s) are linked] → Give the exception constructor the same per-side
shape `DiffContentTooLargeException`/`DiffBinaryContentException` already use (nullable
left/right indicator), and have `ShowVersion` construct it with only "this version" filled in.
Keeps one exception type instead of two nearly-identical ones.

[Changing `GuardNotLinked`'s signature touches three call sites] → Small, mechanical change;
covered by existing restore tests plus new show/diff tests, so a regression would be caught
immediately.
