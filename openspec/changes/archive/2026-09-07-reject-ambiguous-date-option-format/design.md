## Context

See proposal.md - Why. `DateTimeOptionParser.Parse` today calls `DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal)`, which accepts any format `DateTimeOffset.Parse` recognizes under the invariant culture - including `MM/dd/yyyy` slash dates - and only distinguishes "parses" from "doesn't parse," not "parses unambiguously" from "parses under one arbitrary interpretation."

## Goals / Non-Goals

**Goals:**
- Accept only date/time input whose date portion has one unambiguous reading.
- Continue accepting a time-of-day and/or explicit UTC offset alongside the date, since those aren't ambiguous today and nothing in the finding concerns them.
- Reuse the existing `InvalidDateTimeOptionException` for the newly-rejected case, so CLI-layer error handling needs no new exception type.

**Non-Goals:**
- Not adding locale detection or a user-configurable date format preference. The fix is to require a single unambiguous format, not to guess the user's intended locale.
- Not changing how a valid value is resolved to a `DateTimeOffset` (still `AssumeLocal` when no offset is given) - only which input strings are accepted.

## Decisions

**Require ISO 8601 for the date portion, rejecting all slash-separated forms outright** (this proposal's confirmed direction), rather than only rejecting the subset of slash dates that are genuinely ambiguous (e.g. both components ≤ 12). The narrower "reject only genuinely ambiguous values" alternative was considered: it would let `25/12/2025` continue to work (unambiguous, since no month is `25`) while rejecting `01/02/2025`. It was rejected because it's a strictly more complex, easier-to-get-wrong rule (the exact ambiguity boundary is quorum-sensitive, e.g. `12/12/2025` is ambiguous in a different sense - which slot is month? - even though both components fit in a valid month range) - to leave user-facing behavior legible, a single unambiguous rule (ISO only) is simpler to document, simpler to test exhaustively, and simpler for a user to learn once ("always use YYYY-MM-DD") rather than needing to reason about which specific values happen to be safe.

**Implementation approach**: parse with `DateTimeOffset.TryParseExact` against an explicit format list covering ISO 8601 date-only and date-plus-time forms (e.g. `yyyy-MM-dd`, `yyyy-MM-ddTHH:mm:ss`, `yyyy-MM-ddTHH:mm:sszzz`, and the round-trip `"o"`/`"O"` standard format), with `DateTimeStyles.AssumeLocal` preserved for forms that omit an offset, instead of the current permissive `DateTimeOffset.Parse`. Any input that doesn't match one of these exact formats throws `InvalidDateTimeOptionException`, exactly as an unparseable value does today.

## Risks / Trade-offs

- [**BREAKING**: any current user or script passing a slash-separated date to `--at`/`--since`/`--left-at`/`--right-at` will start seeing `InvalidDateTimeOptionException` where it previously (silently, possibly incorrectly) succeeded] → This is the intended fix - those values were never reliably interpreted in the first place, so there is no "previously correct" behavior being broken, only a previously-silent misinterpretation becoming a reported error. The option's `-h`/`--help` text already documents an example value (pre-existing "Date/time options document an example value" requirement), which now becomes the guide to the one accepted format.
- [A user relying on a locale-specific short date format they were used to from elsewhere will need to switch to ISO 8601] → Acceptable one-time adjustment; ISO 8601 is unambiguous across every locale, unlike the format being replaced.
