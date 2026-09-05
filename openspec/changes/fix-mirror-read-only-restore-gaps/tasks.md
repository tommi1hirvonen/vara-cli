## 1. Reproduce both gaps as failing tests

- [ ] 1.1 Add a `FileSystemContentStoreTests` test that stores content, places it at a mirror path via hardlink, then calls `PlaceAtMirrorPath` again for the *same* hash and the *same* mirror path with `previousContentHash` left null, asserting both the mirror file and its blob (via the class's existing `BlobPath` helper) are read-only afterward; verify it fails against the current implementation before any production edit.
- [ ] 1.2 Add a `FileSystemContentStoreTests` test that places two different blobs at two mirror paths via hardlink and then calls `MoveMirrorEntry` from one onto the other (an occupied, read-only destination), asserting the move does not throw and the destination ends up holding the moved entry's content; verify it fails with `UnauthorizedAccessException` against the current implementation before any production edit.

## 2. Fix `PlaceAtMirrorPath`

- [ ] 2.1 In `FileSystemContentStore.PlaceAtMirrorPath`, after the final `File.Move(stagingPath, mirrorPath, overwrite: true)` and before the existing `previousContentHash` restore, call `EnsureReadOnlyIfPresent(mirrorPath)` when the placement went through the hardlink branch (`placedViaHardlink`), leaving the copy-fallback branch untouched; verify the test from task 1.1 now passes.
- [ ] 2.2 Add a code comment at that call explaining why the pre-move `SetAttributes` on the staged file is not sufficient on its own (when the existing mirror entry is a hardlink to the same blob, the staged file, the mirror path, and the blob are one underlying file, so `ClearReadOnlyIfPresent(mirrorPath)` un-marks the staged file too - see design.md); verify the comment names the same-blob case explicitly so a future reader cannot mistake the call for a redundant no-op.
- [ ] 2.3 Confirm the existing `previousContentHash` restore is retained unchanged and still runs after the new re-assert, by re-running the existing deduplicated-sibling tests (`PlaceAtMirrorPath` overwrite case) and confirming they still pass.

## 3. Fix `MoveMirrorEntry`

- [ ] 3.1 In `FileSystemContentStore.MoveMirrorEntry`, capture the source file's `FileAttributes.ReadOnly` state before the move, call `ClearReadOnlyIfPresent(toPath)` before `File.Move(fromPath, toPath, overwrite: true)`, and call `EnsureReadOnlyIfPresent(toPath)` after the move only when the captured source state was read-only; verify the test from task 1.2 now passes.
- [ ] 3.2 Extend the task 1.2 test (or add a sibling test) asserting the relocated entry is still read-only at its new path after overwriting an occupied destination, and add a test asserting a *writable* (copy-fallback) source entry relocated onto an occupied destination stays writable, so the fix cannot silently upgrade a copy-fallback entry's attribute; verify both pass.
- [ ] 3.3 Add a code comment in `MoveMirrorEntry` recording the residual limitation from design.md's Risks - the destination clear can leave the *displaced* content's blob and any deduplicated sibling writable, because the method has no hash identifying that content - so the omission reads as a deliberate, bounded trade-off rather than an oversight.
- [ ] 3.4 Confirm the existing crash-recovery early return (`!File.Exists(fromPath) && File.Exists(toPath)`) and the existing read-only-source move test still behave as before, by re-running those tests.

## 4. Update the archived change's stale design claim

- [ ] 4.1 Correct the assertion in `openspec/changes/archive/2026-09-05-protect-hardlinked-mirror-files/design.md` that `MoveMirrorEntry` "renames an entry to a *new, not-yet-existing* destination path (verified empirically), so ... it needs no read-only-clearing logic of its own", noting that the occupied-destination case exists and is handled by this change; verify the archived document no longer states a claim contradicted by the shipped code.

## 5. Verification

- [ ] 5.1 Run the `Vara.Infrastructure.Tests` suite and confirm every test passes, including all pre-existing `FileSystemContentStore` read-only, hardlink-fallback, move, remove, prune, and cleanup tests.
- [ ] 5.2 Run the full solution test suite (`dotnet test`) and confirm no regression across `Vara.Core.Tests`, `Vara.Application.Tests`, `Vara.Infrastructure.Tests`, and `Vara.IntegrationTests`.
- [ ] 5.3 Run `openspec validate fix-mirror-read-only-restore-gaps --strict` and confirm it reports no errors.
