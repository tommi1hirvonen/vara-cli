## Context

See `proposal.md` for motivation. `SnapshotHistoryService.RestoreAsOf`/`RestoreVersion` (added by
the `initial-implementation` and `browse-and-restore-ux` changes) already restore exactly one
file, resolved to a single `FileVersionRecord`, guarded by `GuardDestination` (mirror-containment
+ per-file overwrite confirmation) and instrumented by the `add-restore-progress-reporting`
change's single-task progress bar (`RestoreProgressReporter`, reused by `RestoreCommand`).

The `browse-and-restore-ux` change already added exactly the repository primitives a directory
restore needs, for `browse`'s point-in-time directory listing: `ISnapshotRepository.GetStateAsOf`
returns every path tracked as live as of a given date (a path not yet added, or already deleted,
by that date is absent), and `GetCurrentState` returns the same shape for "now". Both are already
consumed by `SnapshotHistoryService.ListDirectory` with prefix-matching helpers
(`NormalizeDirectoryPrefix`, `TryGetImmediateChild`) that a directory restore can reuse (generalized
to match every descendant, not just immediate children).

## Goals / Non-Goals

**Goals:**
- Compute the full write/remove plan for a directory restore up front, from manifest data alone,
  before any file is touched - so the single confirmation's reported counts are exact, and the
  mirror-containment guard can be checked for every computed destination before any change is made
  (fail the whole operation atomically rather than partway through).
- Reuse the existing single-file restore's building blocks (`IContentStore.ExtractTo`,
  `IsWithinMirror`, `TargetExists`, `AbsolutePathMirrorMapper`, `RestoreProgressReporter`) rather
  than introducing a parallel implementation.
- Removal is strictly limited to paths the manifest itself currently considers live under the
  requested directory - never a file the system has no tracked record of, even if it happens to
  sit at a computed destination path.

**Non-Goals:**
- No dry-run/preview mode beyond the single confirmation's reported counts. A user who wants to
  see exactly which files are involved can already run `browse`/`history` first.
- No progress indication for the removal pass itself - removals are lightweight (no byte content
  transferred), consistent with the `progress-reporting` capability's existing treatment of
  lightweight operations, and are expected to complete near-instantly even for a few thousand
  files.
- No change to single-file restore's existing per-file overwrite-confirmation flow
  (`GuardDestination`'s `DestinationExistsException` path) - that remains exactly as it is; the
  new single confirmation is a separate, mode-specific flow that only applies when `--recursive`
  is given.
- No atomicity/transactional guarantee across the whole directory restore (matches the existing
  single-file restore's own lack of atomicity, called out in `add-restore-progress-reporting`'s
  design.md).

## Decisions

### A `Plan`/`Execute` split, computed entirely from manifest data before touching disk
`SnapshotHistoryService` gains:
```
public sealed record DirectoryRestoreEntry(string RelativePath, string ContentHash, long Size, string DestinationPath);
public sealed record DirectoryRestorePlan(
    IReadOnlyList<DirectoryRestoreEntry> ToWrite,
    IReadOnlyList<string> ToRemove,
    long TotalBytes);

public DirectoryRestorePlan PlanDirectoryRestore(string directoryPath, DateTimeOffset? asOf, string? outRoot, bool inPlace);
public void ExecuteDirectoryRestore(DirectoryRestorePlan plan, Action<long>? onBytesCopied = null, Action<long>? onSizeResolved = null);
```
`PlanDirectoryRestore` resolves the historical set `S` (`GetStateAsOf(asOf)` or `GetCurrentState()`
if `asOf` is `null`, filtered to the directory's prefix - the same prefix-normalization
`ListDirectory` already uses, generalized from "immediate child" to "any descendant"), and the
current set `C` (`GetCurrentState()`, same prefix filter). `ToWrite` is every entry in `S`, each
paired with its computed destination. `ToRemove` is every destination, computed the same way, for
a path present in `C` but absent from `S` **and** for which `contentStore.TargetExists` returns
true (nothing to remove if it was never actually written there). `TotalBytes` sums `S`'s sizes.
Every computed destination (`ToWrite` and `ToRemove` alike) is checked against
`contentStore.IsWithinMirror` during planning; any violation throws
`RestoreDestinationInMirrorException` immediately, before the plan is even returned - so a bad
destination can never cause a partial restore. If `S`, `C`, and a directory-prefix-filtered
`GetTombstones` scan are all empty, `NoSuchDirectoryException` is thrown (reusing the same
exception `ListDirectory` already throws for an untracked directory, and the same "any tracked
path at any point in history" check it already performs).

`RestoreCommand` calls `PlanDirectoryRestore`, shows the single confirmation using
`plan.ToWrite.Count`/`plan.ToRemove.Count`, and only then calls `ExecuteDirectoryRestore` -
mirroring the existing single-file flow's shape (resolve, then act) rather than interleaving
planning and execution.

Alternatives considered: computing and writing/removing in one streaming pass - rejected, because
the single confirmation needs exact counts *before* anything happens, which requires the full
plan up front anyway; a second, separate execution pass adds no real cost since `GetStateAsOf`/
`GetCurrentState` are already whole-profile reads.

### Destination path computed relative to the *requested* directory, not the full mirror path
For `--out <root>`, an entry's destination is `Path.Combine(root, <path with the directory's own
prefix stripped>)` - e.g. requesting `Documents\Notes` restored to `D:\Restored` places
`Documents\Notes\a.txt` at `D:\Restored\a.txt`, not `D:\Restored\Documents\Notes\a.txt`. For
`--in-place`, each entry's destination is `AbsolutePathMirrorMapper.FromMirrorPath(entry path)`
independently - the same per-file mapping already used by single-file in-place restore - so a
directory whose tracked paths span more than one configured source still resolves each file to
its own correct original location.

### Single confirmation replaces per-file overwrite guarding, but the mirror guard still applies unconditionally
Once the single confirmation is accepted (or `--force` given), every `ToWrite` entry is written
unconditionally (overwriting an existing destination file with no further prompt) and every
`ToRemove` entry is deleted unconditionally - there is no per-file `DestinationExistsException` in
this mode. The mirror-containment guard is never bypassed by `--force`, exactly like single-file
restore.

### `IContentStore` gains a symmetric `RemoveExtractedFile` member for the removal pass
`ExtractTo` already writes to an arbitrary destination path outside the mirror; the removal pass
needs the same kind of arbitrary-destination access in the opposite direction. Adding
`void RemoveExtractedFile(string absolutePath)` (a no-op if the file is already absent) keeps all
filesystem access outside the mirror behind `IContentStore`, consistent with how `ExtractTo`/
`TargetExists`/`IsWithinMirror` already centralize it there, rather than having
`SnapshotHistoryService` call `File.Delete` directly.

Alternatives considered: reusing `RemoveFromMirror` - rejected, that method operates on
mirror-relative paths against the mirror root specifically, not arbitrary absolute destinations.

### Progress reporting reuses `RestoreProgressReporter` unmodified, called once for the whole operation
`RestoreCommand` constructs one `RestoreProgressReporter` per directory restore (not one per
file), calls `OnSizeResolved(plan.TotalBytes)` once before executing, and passes its
`OnBytesCopied` straight through to `ExecuteDirectoryRestore`, which forwards it to every
constituent file's `ExtractTo` call in turn. Because the reporter already just accumulates
`_bytesCopiedSoFar` against a fixed `_totalBytes`, iterating multiple files through the same
instance produces exactly the desired single, continuously-advancing bar with no changes to the
reporter itself. The removal pass runs to completion first (fast, no byte progress to report),
then the write pass runs behind the progress bar - a simple two-phase execution order rather than
interleaving removals and writes, since removals have nothing meaningful to show on a byte-based
bar.

Alternatives considered: a count-based indicator (files done / total files) instead of byte-based
- rejected, since file sizes vary widely in a directory restore and the existing byte-based
approach already gives a more accurate progress signal, per the `progress-reporting` capability's
existing preference for byte-based over count-based progress wherever byte totals are known
upfront.

## Risks / Trade-offs

- [Risk] The removal step deletes tracked-but-historically-absent files at their computed
  destination - if the plan's `C \ S` computation were wrong, this could delete more than
  intended → Mitigation: removal is restricted to destinations derived only from paths
  `GetCurrentState` itself reports as currently live under the requested prefix, never from an
  arbitrary filesystem scan of the destination directory; the exact count is surfaced in the
  single confirmation before anything is deleted.
- [Risk] No atomicity across the whole operation - an interruption mid-restore can leave the
  destination in a state that is neither the old nor the new tree → Mitigation: matches the
  existing, already-accepted lack of atomicity for single-file restore; re-running the same
  command re-derives the same plan and safely retries remaining work, since both `ExtractTo`
  (overwrite) and `RemoveExtractedFile` (delete-if-present) are idempotent per entry.
- [Trade-off] Running the removal pass to completion before the write pass (rather than
  interleaving) means removals are invisible to the progress display for their own duration →
  accepted, since removals carry no byte total to show and are expected to complete quickly even
  for large directories (metadata-only filesystem operations).

## Open Questions

- Should a future change add an opt-out (e.g. `--no-prune`) for a user who wants an additive-only
  bulk restore - writing historical files without removing anything currently present? Nothing in
  this design precludes adding such a flag later; the default (full point-in-time reconstruction)
  matches what was explicitly requested for this change.
