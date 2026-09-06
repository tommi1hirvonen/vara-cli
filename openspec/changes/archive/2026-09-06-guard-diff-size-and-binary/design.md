## Context

`SnapshotHistoryService.OpenVersionsForDiff` (`src/Vara.Application/History/SnapshotHistoryService.cs:149`)
resolves each side via `ResolveVersion`, which already returns a `FileVersionRecord` carrying the
version's `Size` from the manifest, then calls `contentStore.OpenRead(hash)` for each side and
hands the two raw `FileStream`s back to `DiffCommand`, which does
`new StreamReader(left).ReadToEnd()` on each before diffing (`src/Vara.Cli/Commands/DiffCommand.cs:73-74`).
Blobs are stored uncompressed and unwrapped (`FileSystemContentStore.OpenRead` returns a plain
`FileStream`), so a bounded read from the start of either stream sees real file bytes with no
decoding needed. See proposal.md for why this needs a guard.

## Goals / Non-Goals

**Goals:**
- Refuse a diff whose either side is too large or binary, before either side's full content is
  read into memory.
- Report both problems as clear, existing-style CLI errors (see `ErrorReporting`), not stack
  traces.
- Keep the check as close to the existing size metadata and stream-opening code as possible,
  rather than introducing a new content-inspection pass over already-fully-read data.

**Non-Goals:**
- No new CLI option to override the size limit or force a diff of a large/binary file. If this
  turns out to be needed, it's a separate, additive change.
- No change to `show`'s or `restore`'s handling of large/binary content - both stay as they are;
  see the "Binary-aware diffing" non-goal already recorded in
  `openspec/specs/snapshot-history` history (2026-09-03 design), which this change does not
  revisit for any command other than `diff`.
- No attempt at more sophisticated binary detection (e.g. encoding sniffing, MIME detection).

## Decisions

**Size limit: 10 MB, checked from already-resolved `FileVersionRecord.Size`.** Both
`ResolveVersion` calls inside `OpenVersionsForDiff` already return this before either
`contentStore.OpenRead` call, so the size check costs nothing beyond a comparison - no stat call,
no stream open. 10 MB comfortably covers legitimate text/code/config files (which this project's
other size-aware paths, like progress-reporting's large-file streaming, treat as unremarkable)
while still bounding worst-case memory to a small, fixed multiple of that (both a `Stream` and
its decoded `string` copy, times two sides). Chosen as a first reasonable default; not exposed as
configurable (see Non-Goals) since no current requirement calls for it, and it is trivial to
raise later if it proves too tight.

**Binary detection: a NUL byte within the first 8000 bytes of each stream.** This is the same
heuristic Git uses (`core.bigFileThreshold`-independent binary check) and needs only a small,
bounded `Read` per side - no full buffering, no encoding-detection library. A false positive
(a legitimate NUL early in a text-like file) is judged an acceptable, well-precedented trade-off
given the existing design's explicit acceptance that binary handling here is best-effort, not
exact.

**Ordering: check both sides' sizes first, then both sides' binary-ness, before opening either
stream for the size check and before reading further for the binary check.** This lets a single
error message name every offending side at once (e.g. "both sides exceed the limit") instead of
failing fast on the first side checked and leaving the user to fix one problem at a time across
repeated invocations - directly the reason for the proposal's "both sides checked" scenarios.

**Where the check lives: inside `OpenVersionsForDiff`, not `DiffCommand`.** The service already
has the size metadata and owns stream-opening; keeping the guard there means `DiffCommand` needs
no new logic beyond letting the new exceptions propagate to `ErrorReporting` like every other
domain exception it already relies on.

**Two new exception types, not one parameterized type.** Matches the existing convention in
`SnapshotExceptions.cs` (e.g. `NoMatchingVersionException` vs. `NoHistoryForPathException`) of a
distinct exception per distinct refusal reason, each with a purpose-built message and the
resolved side(s)/size(s) as properties for any future programmatic use:
- `DiffContentTooLargeException`: carries the relative path and, for each oversized side, its
  size (a side is included in the message only if it was actually oversized - if only one side
  exceeds the limit, only that side is named).
- `DiffBinaryContentException`: carries the relative path and which side(s) were detected binary.

If a diff hits both problems on the same or different sides, size is checked (and reported)
first - a version's binary-ness is not even sampled until both sides are confirmed within the
size limit, since sampling requires a stream read this design otherwise avoids for an
already-doomed comparison.

## Risks / Trade-offs

- **[Risk] A legitimate text file happens to contain a stray NUL byte in its first 8000 bytes** →
  it gets refused as "binary" even though a human would call it text. Mitigation: this is the
  same trade-off Git itself accepts; no CLI override is added in this change, but the design
  doesn't preclude adding one later if it proves painful in practice.
- **[Risk] A text file just over 10 MB (e.g. a large generated log or SQL dump) can no longer be
  diffed** → user must fall back to `show`-ing both versions and diffing them externally.
  Mitigation: 10 MB is a first default, not a hard architectural limit; raising it later is a
  one-line change with no spec impact beyond the number itself.
- **[Trade-off] Reading a small sample from each stream to check for binary content means the
  stream's read position moves before the (already size-guarded) full read.** Mitigation: both
  streams are freshly opened `FileStream`s seeked to position 0 by `OpenRead`; the binary-check
  read is immediately followed by seeking back to the start (or re-opening) before the real
  `StreamReader.ReadToEnd`, so the diffed content is unaffected.
