## Context

`DirectoryFileSystemScanner.Scan` returns a `ScanResult` with both `Entries` (successfully scanned files/links) and `Failures` (paths that couldn't be scanned - `ScanFailureReason.UnreadableDirectory`/`UnreadableEntry`). `BackupPipeline.Run` calls `BackupDiffer.Diff(scanResult.Entries, currentState)` - `Failures` is never passed in. `Diff` computes `DeletedPaths` as every `currentState` key not seen in this run's scanned paths, so any manifest-tracked path absent from `Entries` - for any reason, including a scan failure - is classified deleted. See proposal.md for why this is a problem and the observed impact.

Manifest paths (`CurrentFileState.RelativePath`) and `ScannedEntry.RelativePath` are both mirror-space paths produced by `AbsolutePathMirrorMapper.ToMirrorPath`, which only strips the drive-letter colon and otherwise preserves the absolute source path unchanged. This means a source's own path, run through the same mapper, is always a prefix of every mirror path it produces - no separate source-to-manifest-path tracking exists or is needed today.

## Goals / Non-Goals

**Goals:**
- Any path, subtree, or whole source that a scan run could not verify is left completely alone in the mirror and manifest for that run - never classified deleted.
- Cover all three failure situations uniformly: a missing/misconfigured source root, a subtree that can't be enumerated, and a single file that can't be read.
- Keep the fix confined to the scan -> diff boundary; no change to how failures are reported to the CLI/user (`BackupPipeline` already surfaces `scanResult.Failures` in `BackupRunResult.FailedPaths`), no change to move detection or the executor's delete/move handling.

**Non-Goals:**
- Making the run fail (non-zero exit / failed snapshot) when a source is unavailable. It continues to be reported as a failure like any other unreadable path, consistent with the existing "Unreadable files do not abort the run" requirement's spirit; escalating to a hard error was considered (see Decisions) and rejected for this change.
- A `revert` command or any other recovery-after-the-fact mechanism - tabled per prior discussion; this change addresses prevention, not recovery.
- Detecting or handling source-path overlap between two configured sources - out of scope; `Profile` already validates target/source overlap but not source/source overlap, and that's a pre-existing, unrelated gap.

## Decisions

### Suppress by mirror-path prefix, not by tracking source ownership

Instead of teaching the manifest or the differ which source produced which path, each `ScanFailure` carries the mirror-space path of the location that failed (the failed source's root, the failed directory, or the failed file - whichever is coarsest for that failure). `BackupDiffer.Diff` excludes a `currentState` path from `DeletedPaths` when it equals, or is nested one path-segment boundary under, any failure's mirror path - the same "equals or starts-with `prefix + separator`" idiom `DirectoryFileSystemScanner.IsExcluded` already uses for exclude-list matching.

Alternative considered: track which source each manifest row belongs to (e.g., a `SourceId` column) and suppress by source instead of by path prefix. Rejected - it's a manifest schema change for information the mirror-path mapping already encodes for free, and it wouldn't naturally extend to subtree-level (`UnreadableDirectory`) or file-level (`UnreadableEntry`) failures without also carrying a sub-path, which is exactly what the chosen approach already does.

### Compute the mirror-space failure path at the scanner, not the differ

`DirectoryFileSystemScanner` already has the absolute path of the failed source root, directory, or file at every point it constructs a `ScanFailure` today (or trivially can, for the new `SourceUnavailable` case). Running that same absolute path through `AbsolutePathMirrorMapper.ToMirrorPath` - the identical transform already applied to every successful `ScannedEntry` - costs nothing extra and keeps `ScanFailure` self-contained: the differ needs no knowledge of `Source` objects or profile configuration to do the suppression.

`ScanFailure.RelativePath` (existing) stays source-relative and is kept only for human-facing reporting (it reads naturally, e.g. `subdir\locked.txt`, whereas a mirror path looks like a mangled absolute path, e.g. `C\Users\john\subdir\locked.txt`). A new field carries the mirror-space value. Alternative considered: repurpose `RelativePath` itself to be mirror-space. Rejected - it would change `BackupPipeline`'s existing `FailedPaths` reporting output for every existing failure kind, a user-visible regression unrelated to this fix's purpose.

### `ScanFailureReason.SourceUnavailable` is its own reason, not reused `UnreadableDirectory`

A missing source is a distinct situation from a present-but-unreadable directory (no ACL/lock is involved; the path simply doesn't resolve to anything), and CLI/log output benefits from saying so explicitly rather than reporting a phantom "directory" that was never there. The suppression logic in the differ treats all three reasons identically, so this costs nothing beyond an enum member and the one new scan-time check.

### Suppression applies uniformly to all three `ScanFailureReason` values

Confirmed (see proposal.md) that `UnreadableDirectory` and `UnreadableEntry` already have the same flaw at smaller scope - a locked file or permission-denied subtree is currently misclassified as deleted today. Fixing only the new `SourceUnavailable` case and leaving the other two would leave a known, just-diagnosed bug sitting unaddressed next to its fix. Threading `Failures` into `Diff` costs the same regardless of how many reasons use it, so there's no reason to scope it narrower.

### A hard error for a missing source was considered and rejected

The proposal's origin (an AI review finding) suggested treating a missing source as a hard error as one option. Rejected for this change: a transient failure (a temporarily unplugged drive during a scheduled run) would then abort the *entire* run - including sources that are perfectly reachable - rather than degrading gracefully, which contradicts the existing "Unreadable files do not abort the run" requirement's intent. Recording it as a `ScanFailure` (this change) keeps behavior consistent with how every other unreadable-path case is already handled, while still fully closing the silent-mass-deletion hole. A future change (this proposal doesn't preclude it) could still add a stronger signal, e.g. a highlighted CLI warning specifically for `SourceUnavailable`.

## Risks / Trade-offs

- [Risk] A path suppressed this way is invisible in `DeletedPaths` for the run, so if the file truly was deleted while its source/subtree was unreachable, the mirror keeps stale content for one extra run (until the source becomes reachable again and the differ can confirm the deletion) → Mitigation: this is the intended, safe direction of the trade-off - a stale mirror entry is trivially recoverable (delete it manually or wait one run), whereas an incorrectly deleted one is not, absent history. The failure is still reported every run it recurs, so it isn't silent.
- [Risk] Widening suppression to `UnreadableDirectory`/`UnreadableEntry` changes already-shipped, tested behavior (those paths previously *were* deleted from the mirror on a permission error or lock) → Mitigation: existing tests (`DirectoryFileSystemScannerTests`) only assert scanner-level behavior (entries/failures), not the differ's downstream classification, so no existing test asserts the old (buggy) deletion behavior; this is confirmed to be an untested gap being closed, not a documented behavior being reversed.
- [Trade-off] Prefix-matching on mirror paths assumes a failure's mirror path always aligns on a path-segment boundary with any manifest path it should suppress (i.e., never a partial-segment false match, such as `C\Users\john` wrongly matching `C\Users\john2\...`). Mitigated by using the same segment-boundary-aware comparison (`equals`, or `starts-with prefix + separator`) already proven correct for exclude-list matching.

## Migration Plan

No data migration. This changes in-process classification logic only; no manifest schema or on-disk format changes. Deployed as a normal code change - the next backup run after upgrade immediately benefits (a previously-failing source stops mass-deleting its mirror subtree). No rollback concerns beyond reverting the code change.
