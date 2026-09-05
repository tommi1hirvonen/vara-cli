## Context

See proposal.md - Why. Two facts from the archived `protect-hardlinked-mirror-files` change carry over unchanged and drive this design:

- On NTFS, `FileAttributes.ReadOnly` lives on the underlying file record, not on an individual hardlink name. Setting or clearing it through *any* name changes what *every* hardlink to that data reports, including the canonical blob under `.vara\versions\`.
- `File.Move(..., overwrite: true)` and `File.Delete` both throw `UnauthorizedAccessException` against a read-only destination/target, so any code path that overwrites or deletes a possibly-hardlinked mirror entry must clear the attribute first.

`PlaceAtMirrorPath` today: stage (hardlink or copy) → set the staged file's attribute per placement kind → `ClearReadOnlyIfPresent(mirrorPath)` → `File.Move(staging, mirrorPath, overwrite: true)` → `EnsureReadOnlyIfPresent(BlobPath(previousContentHash))` when the caller supplied a previous hash. `MoveMirrorEntry` today: early-return if the relocation already happened, then `File.Move(from, to, overwrite: true)` with no attribute handling at all.

Both gaps were reproduced against the current implementation with throwaway tests before this change was written:

1. **Same-blob re-placement wipes protection.** When `mirrorPath` is already a hardlink to the *same* blob as the incoming content, the staged file, `mirrorPath`, and the blob are all one underlying file. Setting read-only on the staged file marks that shared record; `ClearReadOnlyIfPresent(mirrorPath)` then clears the very same record - un-marking the staged file too. The move therefore installs a writable mirror entry over a now-writable blob. With `previousContentHash` null (every `Add` operation), nothing restores it, and the loss is permanent for that content. This is reachable through the interruption path the codebase already handles elsewhere: a run that wrote the mirror entry but never committed its manifest row leaves the next run to plan that path as `Add`, with the same content.
2. **Move onto an occupied destination now throws.** The archived design recorded that `MoveMirrorEntry` "renames an entry to a new, not-yet-existing destination path", but the method passes `overwrite: true` deliberately and its own doc comment describes the interrupted-run case where the destination already exists. Against a read-only destination the move now throws `UnauthorizedAccessException`, which `BackupExecutor` catches and records as a failed path - and because the destination stays read-only, it fails identically on every subsequent run.

## Goals / Non-Goals

**Goals:**
- Make a hardlink placement's protection follow from the placement itself, so it cannot be defeated by the destination-clearing step or by a caller that has no previous-content hash to supply.
- Restore `MoveMirrorEntry`'s pre-existing ability to overwrite an occupied destination, and preserve the relocated entry's own protection across that move.
- Fix both without changing any interface signature, any planner/executor plumbing, or which placements are hardlinked.

**Non-Goals:**
- Revisiting the `previousContentHash` plumbing added by the archived change. It remains necessary and correct: it re-protects the *superseded* content's blob and any sibling mirror path deduplicated against it, which a fix scoped to the placed path alone cannot reach.
- Any new Win32/NT interop (for example, querying file IDs via `GetFileInformationByHandle` to detect the same-underlying-file case, or `FILE_DISPOSITION_FLAG_IGNORE_READONLY_ATTRIBUTE`) - rejected below.
- Closing the residual gap where `MoveMirrorEntry` cannot re-protect the blob of the *displaced* destination content (see Risks).
- Detecting deliberate tampering (`attrib -r` then edit). Unchanged non-goal: read-only is friction and a signal, not an enforced boundary.

## Decisions

**Re-assert read-only on the final mirror path after the move, for every hardlink placement.** After `File.Move(stagingPath, mirrorPath, overwrite: true)` succeeds, `PlaceAtMirrorPath` calls `EnsureReadOnlyIfPresent(mirrorPath)` whenever the placement went through the hardlink branch. This is the smallest change that makes the invariant "a hardlinked mirror entry is read-only" hold unconditionally at method exit, rather than holding only when the destination-clearing step happened not to touch the same underlying file.

  Why the final path rather than `BlobPath(hash)`: after a hardlink placement they are the same underlying file, so either name re-protects both, but `mirrorPath` states the invariant the spec actually expresses ("this mirror entry is read-only") and does not depend on the blob still being present. It is a no-op in the overwhelmingly common case, where the attribute survived the earlier steps untouched.

  Why not simply skip the destination clear when the destination is the same underlying file as the staged one: that requires opening both files and comparing volume/file IDs through new interop, adds a failure mode of its own, and buys nothing - the unconditional re-assert is one attribute read plus, rarely, one write.

  Why not make `previousContentHash` required instead: it does not fix the case. The planner genuinely has no previous content hash for an `Add` operation, because there is no manifest row for that path - that absence is exactly the crash-recovery scenario, not an oversight to be plumbed around.

**Keep setting the attribute on the staged file before the move as well.** The post-move re-assert does not make the pre-move set redundant. The pre-move set keeps the attribute inside the existing "prepare, then atomically swap" structure, so a crash between the move and the re-assert still leaves a read-only mirror entry in every case except the same-blob one this change fixes. Keeping both keeps the crash-exposure window no wider than it is today.

**`MoveMirrorEntry` clears the destination, then restores the relocated entry's own attribute.** The sequence becomes: read the source's attributes before moving (so the read-only state being carried is known), `ClearReadOnlyIfPresent(toPath)`, `File.Move(fromPath, toPath, overwrite: true)`, and finally `EnsureReadOnlyIfPresent(toPath)` if the source was read-only.

  The post-move step is required, not defensive: when the source and the displaced destination are hardlinks to the same blob (the likeliest occupied-destination case, since it arises from an interrupted run that already performed part of this same relocation), clearing the destination also clears the source, and the moved entry would otherwise land writable - the same shared-record trap as gap 1.

  Reading the source's attribute first, rather than unconditionally re-asserting on `toPath`, preserves the existing contract that a copy-fallback entry stays writable: a move must carry its entry's attribute forward as-is, not upgrade a writable entry into a read-only one.

**No signature or plumbing changes.** Both fixes are local to `FileSystemContentStore` and use the existing `ClearReadOnlyIfPresent`/`EnsureReadOnlyIfPresent` helpers. `IContentStore`, `PlannedOperation`, `BackupPlanner`, and `BackupExecutor` are untouched, so the test doubles and their call sites are untouched too.

**Cover both gaps with tests that reproduce them first.** Both failures were confirmed with real filesystem tests against the current code before designing the fix; those same scenarios become permanent regression tests in `FileSystemContentStoreTests`, using the existing `ForceHardlinkSupportForTesting`/`ForceNextHardlinkFailureForTesting` seams and the class's existing `BlobPath` test helper. No new test infrastructure is needed.

## Risks / Trade-offs

- [`MoveMirrorEntry` still cannot re-protect the blob of the content it *displaces* when it overwrites an occupied destination: clearing that destination's attribute clears the shared record of its content's blob and any sibling mirror path deduplicated against it, and the method has no hash identifying that displaced content] → Accepted and documented in code. Giving `MoveMirrorEntry` such a hash would mean plumbing the hash of whatever happens to occupy the destination, which the planner does not know (the destination is by definition an `Add` path with no manifest row). The exposure is narrow: it requires an occupied move destination, which only arises from an interrupted run, and in that scenario the displaced content is almost always the same blob as the moved entry - which the post-move re-assert protects anyway. Any later run that places, changes, or removes a path referencing the affected blob re-asserts its protection through the existing `EnsureReadOnlyIfPresent` calls.
- [A crash between `File.Move` and the post-move re-assert leaves the mirror entry writable] → Unchanged from today's exposure for every case except the same-blob one, since the staged file is still marked before the move; and self-healing, because the next run re-places or re-asserts that path. Not worth a heavier transactional mechanism for an attribute that is explicitly a friction signal.
- [The extra `EnsureReadOnlyIfPresent` adds an attribute read per placement] → Negligible against the file copy or hardlink plus rename each placement already performs, and it writes only when the attribute is actually missing.
- [A future contributor could reintroduce gap 1 by moving or removing the post-move re-assert, since it is a no-op in nearly every test scenario] → Mitigated by a regression test whose entire point is the same-blob re-placement path, plus a code comment stating why the pre-move set is not sufficient on its own.

## Migration Plan

None. The change is behavior-preserving for every already-correct path, needs no data migration, and requires no coordinated rollout - a mirror written by the current code is fixed up the next time each affected path is placed. Rollback is a straight revert of the two edited methods.
