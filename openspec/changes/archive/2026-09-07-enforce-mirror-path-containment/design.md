## Context

See proposal.md - Why/What Changes for the bug and the chosen direction (containment guard at
the write boundary, not a UNC rejection at config-validation time).

Relevant existing code:
- `FileSystemContentStore` (`src/Vara.Infrastructure/Storage/FileSystemContentStore.cs`) already
  has `IsWithinMirror(string absolutePath)`, which resolves `_mirrorRoot` via `Path.GetFullPath`
  and does a trailing-separator-safe prefix check. It is currently only consumed by the
  restore/snapshot-history capability (`SnapshotHistoryService.cs:406,414,585`) to stop a restore
  from writing into the live mirror - it is never consulted by the backup-side mirror-write
  methods (`PlaceAtMirrorPath`, `MoveMirrorEntry`, `RemoveFromMirror`), which is exactly the gap
  this change closes.
- `BackupExecutor` (`src/Vara.Application/Backup/BackupExecutor.cs`) already wraps every
  `PlaceAtMirrorPath`/`MoveMirrorEntry`/`RemoveFromMirror` call in
  `catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)`, recording the
  operation's `RelativePath` as failed and continuing the run (its existing "locked file" handling
  per the backup-execution spec's "Unreadable files do not abort the run" requirement).
- `tests/Vara.Application.Tests/Backup/Fakes.cs`'s `FakeContentStore` already exposes settable
  "throw this exception instead" seams for `MoveMirrorEntry` and `RemoveFromMirror` (and threads
  exceptions through `PlaceAtMirrorPath` the same way `BackupExecutorTests` already exercises for
  locked-file scenarios), so the existing per-operation failure-recording behavior is already
  covered by tests we can extend rather than a new mechanism we need to build.

## Goals / Non-Goals

**Goals:**
- Guarantee that `PlaceAtMirrorPath`, `MoveMirrorEntry`, and `RemoveFromMirror` never touch a
  filesystem path outside `_mirrorRoot`, regardless of what relative path a caller supplies.
- Make the failure visible and actionable (recorded per-file failure) instead of silent.
- Reuse the existing per-operation failure-recording path in `BackupExecutor` unchanged.
- Leave every currently-passing scenario (drive-letter mirroring, hardlink/read-only handling,
  move crash-recovery, restore's own `IsWithinMirror` guard) byte-for-byte unaffected.

**Non-Goals:**
- Adding real UNC-to-mirror-path mapping in `AbsolutePathMirrorMapper` (a "support network
  sources properly" feature, deliberately left for a later change - see proposal.md).
- Any change to `Profile`/`Source`/`YamlProfileConfigLoader` validation. UNC source paths keep
  loading successfully; this change only affects what happens when the content store is asked to
  write their (currently unmapped) entries.
- Changing restore-side behavior or `SnapshotHistoryService`'s existing `IsWithinMirror` usage.

## Decisions

### One shared private helper, not three duplicated checks
Add a private `FileSystemContentStore` helper, e.g.
`ResolveMirrorPath(string mirrorRelativePath)`, that: combines `_mirrorRoot` with the given
relative path, resolves it with `Path.GetFullPath`, and either returns the resolved absolute path
or throws (see below) if it falls outside `_mirrorRoot`. It reuses the same resolution and
trailing-separator-safe prefix comparison `IsWithinMirror` already performs, refactored into a
shared `private static bool IsPathWithinRoot(string root, string candidate)` used by both
`IsWithinMirror` and the new helper, so the containment rule is defined in exactly one place.
`PlaceAtMirrorPath`, `MoveMirrorEntry` (for both `fromPath` and `toPath`), and `RemoveFromMirror`
replace their current `Path.Combine(_mirrorRoot, ...)` call with this helper.
- **Alternative considered**: inline the check at each of the four call sites. Rejected - it
  triplicates the same logic `IsWithinMirror` already has, and any future fourth mirror-write
  method would risk forgetting the check.

### Throw a dedicated `IOException` subclass, not a generic `IOException` or a new exception family
Add `MirrorPathEscapesTargetRootException : IOException` in `Vara.Core.Abstractions` (alongside
`IContentStore`, since it is part of that port's documented contract, not an
infrastructure-only implementation detail), carrying the offending relative path and the resolved
absolute path for a clear message (e.g. `Mirror path 'file.txt' resolves to '\\srv\share\file.txt',
which is outside the target root 'D:\Backup'.`). Thrown from `ResolveMirrorPath` when containment
fails.
- Being an `IOException` subclass means it is caught by `BackupExecutor`'s existing
  `catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)` filter with zero
  changes to `BackupExecutor` - the pattern match is on the runtime type, which includes
  subclasses.
- **Alternative considered**: throw a plain `IOException` with just a message. Rejected - a typed
  exception lets tests (and any future caller that wants to distinguish this failure from a
  locked-file `IOException`) assert on the specific condition without string-matching a message.
- **Alternative considered**: define it as its own exception family unrelated to `IOException`
  (mirroring `ProfileConfigException`'s style for config errors). Rejected - `BackupExecutor`'s
  failure handling is keyed on `IOException`/`UnauthorizedAccessException`, and this failure is,
  semantically, exactly that class of thing ("could not write this path") - introducing a
  parallel catch path would be a second mechanism doing the same job the first already does well.

### The check runs on every call, not gated behind a "looks suspicious" heuristic (e.g. `IsPathFullyQualified` on the relative path)
`ResolveMirrorPath` always resolves and checks containment - it does not try to special-case UNC
paths, `..` segments, or any other pattern first. This keeps the fix correct for *any* way a bad
relative path could arise (today: an unmapped UNC source; in principle: a corrupted manifest row),
per the proposal's "regardless of why" framing, and keeps the implementation trivially small.
- **Alternative considered**: reject only relative paths starting with `\\` (UNC) or containing
  `..`. Rejected - narrower, easy to bypass with any other malformed input, and duplicates
  intent that `Path.GetFullPath` + a prefix check already expresses correctly in one general rule.

### `MoveMirrorEntry` checks both endpoints before doing anything
Both `fromPath` and `toPath` are resolved via `ResolveMirrorPath` up front, before the existing
`File.Exists`/`File.Move` logic runs, so a violation on either endpoint aborts the operation
before any filesystem mutation - consistent with `PlaceAtMirrorPath`/`RemoveFromMirror` never
partially acting on a rejected path either.

## Risks / Trade-offs

- **[Risk]** A UNC source, which previously appeared to "succeed" (silently, with no mirror
  entries), will now report every one of its entries as failed on every run, which is a visible
  behavior change for anyone currently (accidentally) relying on it. → **Mitigation**: This is the
  intended fix - the old behavior was corrupting the source file and hiding data loss. The failure
  is reported the same way any other unreadable-source failure already is today (existing
  `--verbose`/summary reporting of failed paths), so there is no new reporting surface to build.
- **[Risk]** `Path.GetFullPath` itself can throw for certain malformed inputs (e.g. embedded
  invalid characters) before containment is even evaluated. → **Mitigation**: `PlaceAtMirrorPath`/
  `MoveMirrorEntry`/`RemoveFromMirror` are already called from within `BackupExecutor`'s existing
  `catch (IOException or UnauthorizedAccessException)` per-operation guard; `Path.GetFullPath`'s
  own failure modes (`ArgumentException`/`PathTooLongException`/`NotSupportedException`) are
  edge cases pre-existing today wherever `Path.Combine`/`Path.GetFullPath` is already called in
  this class (e.g. `IsWithinMirror`), so this change does not introduce a new unhandled-exception
  risk beyond what already exists.
- **[Trade-off]** The fix does not make UNC sources actually work - it only stops them from
  silently corrupting data. Anyone who wants network-share backups still needs the follow-up
  `AbsolutePathMirrorMapper` change. This is accepted scope, per proposal.md.
