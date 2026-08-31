## Context

`FileSystemContentStore` (`src/Vara.Infrastructure/Storage/FileSystemContentStore.cs`) chooses
hardlink-vs-copy once per instance: `ProbeHardlinkSupport()` runs a single throwaway link at
startup and caches the result in `SupportsHardlinks`. `PlaceAtMirrorPath` then branches on that
cached bool for every call, with no per-call error handling around `Kernel32.CreateHardLink`.

NTFS caps the number of hard links a single file can have at 1024. A profile with many
byte-identical files (duplicate empty files, marker files, repeated config templates) can push a
single content-addressed blob past that cap. When it does, `CreateHardLink` throws `IOException`
(wrapping the Win32 error, currently undifferentiated by cause), which propagates out of
`PlaceAtMirrorPath` uncaught. `BackupExecutor.ExecuteTransfer` catches it, increments `Failed`,
and - critically - skips `repository.RecordFileVersion` for that operation. With no manifest
entry written, the next run's planner sees the file as still needing to be added, retries the
identical hardlink, and fails again: a permanent, run-over-run failure for every mirror path
that would exceed the cap for that blob. See proposal.md - Why.

## Goals / Non-Goals

**Goals:**
- Placing a blob at a mirror path never fails outright merely because that blob's hard-link
  count is exhausted, as long as a real copy of the content can still be written.
- The fallback is decided per placement, not cached per store instance, so it self-recovers if
  the blob's link count later drops below the limit (e.g. after pruning removes other mirror
  paths referencing it).
- No change to the manifest schema, `IContentStore`'s method signatures, or the storage layout.

**Non-Goals:**
- Distinguishing "hard-link limit reached" from other hardlink-creation failures (e.g.
  cross-volume, permission-denied) by inspecting the specific Win32 error code. Both are handled
  identically to keep the fix small; see Decisions.
- Proactively tracking or predicting a blob's link count to avoid attempting a doomed hardlink.
  The OS is the source of truth; we just react to its answer.
- Changing `ProbeHardlinkSupport`'s volume-level probe or the exFAT-style "no hardlink support at
  all" fallback path, which already works correctly and is unaffected by this change.

## Decisions

**Catch broadly around the hardlink attempt, not narrowly on the specific Win32 error code.**
`PlaceAtMirrorPath` wraps only the `Kernel32.CreateHardLink` call in a try/catch for `IOException`
and falls back to `File.Copy` on any failure, rather than parsing `Marshal.GetLastWin32Error()`
for `ERROR_TOO_MANY_LINKS` (1142) specifically. Alternative considered: check the specific error
code and only fall back for that case, re-throwing everything else. Rejected because: (a) it adds
interop-level error-code parsing for one extra case, (b) if the real cause were something else
transient (e.g. an antivirus lock on the link operation specifically), falling back to a copy is
still a reasonable, safe recovery - and if the copy also fails, `BackupExecutor`'s existing
catch-and-record-failure path still applies, so no failure mode is silently swallowed.

**No caching of "this blob needs a copy" per hash.** Unlike the per-instance
`SupportsHardlinks` probe (a stable fact about the volume for the run's lifetime), a blob's link
count is not stable - it can drop if other mirror paths referencing it are later removed (moves,
deletes, pruning). Every placement attempts a hardlink first when `SupportsHardlinks` is true, and
only falls back to a copy for that one call if it fails. This keeps the store self-healing without
extra state, at the cost of one wasted syscall on every placement that still exceeds the cap - an
acceptable trade given placements are not a hot path per-byte (only per-file).

**Fallback happens inside `FileSystemContentStore.PlaceAtMirrorPath`, not in
`BackupExecutor`.** `BackupExecutor` already treats "hardlink vs. copy" as an implementation
detail it doesn't need to know about (`IContentStore.PlaceAtMirrorPath`'s existing contract:
"Uses a hardlink when supported, otherwise a real copy"). Keeping the fallback inside the content
store preserves that abstraction boundary - the interface doc comment is updated to mention the
per-placement fallback, but the method signature is unchanged.

**Testing seam.** Real reproduction of the 1024-link cap in a test is slow, NTFS-specific, and not
worth the disk churn. Following the existing pattern of `ForceHardlinkSupportForTesting` (used to
force the volume-level decision without a real non-hardlink volume), a similar internal seam will
let a test force a single `PlaceAtMirrorPath` call's hardlink attempt to fail so the copy-fallback
path can be exercised deterministically without depending on real link-count exhaustion.

## Risks / Trade-offs

- [Broad catch could mask a genuine, non-link-count hardlink failure (e.g. destination path
  problem) behind a successful copy, changing what gets surfaced as a failure] → Acceptable: if
  the underlying condition also breaks `File.Copy` (e.g. destination directory unwritable), the
  existing `BackupExecutor` catch still records a failure. If it doesn't (e.g. only hardlink
  creation was blocked), succeeding via copy is the correct outcome per the "graceful
  degradation" requirement already established for whole-volume hardlink absence.
- [Per-placement retry-then-fallback adds one extra failed syscall for every mirror path beyond
  the cap, every run, for as long as the blob stays over-referenced] → Acceptable: syscall cost is
  negligible relative to the file I/O already being skipped (this only applies to files whose
  content is unchanged and already deduplicated); no polling or background recomputation is
  introduced.
- [Mirror paths placed via copy fallback no longer share physical storage with the blob they
  represent, increasing disk usage for over-referenced content beyond what hardlinking would use]
  → Inherent to the underlying OS constraint, not something this change can avoid; the
  alternative (permanent failure) is strictly worse.

## Migration Plan

No data migration needed - this only changes in-process error-handling behavior on the next run.
Existing failed-file entries from before this fix are not retroactively fixed by this change
alone; they will succeed automatically on the next backup run once the fallback is deployed, since
the affected files are still re-planned as pending adds (no manifest entry was ever recorded for
them).
