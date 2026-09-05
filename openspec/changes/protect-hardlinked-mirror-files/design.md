## Context

See proposal.md - Why. `PlaceAtMirrorPath` (`src/Vara.Infrastructure/Storage/FileSystemContentStore.cs`) currently has two placement paths that converge on the same final step - `File.Move(stagingPath, mirrorPath, overwrite: true)`:

- **Hardlink path** (`SupportsHardlinks` true, and no forced/per-blob failure): `Kernel32.CreateHardLink(stagingPath, blobPath)` stages a hardlink to the blob, then the atomic rename places it at `mirrorPath`. The resulting mirror file *is* the blob (same physical file/inode).
- **Copy-fallback path** (`SupportsHardlinks` false, or the hardlink attempt threw `IOException` - most notably NTFS's 1024-hardlink-per-file cap): `CopyWithProgress` streams the blob's bytes into a brand-new file at `stagingPath`, then the same atomic rename places it. The resulting mirror file is an independent physical copy.

Today neither path touches `FileAttributes` at all - both produce a normal writable file. Per the existing "Graceful degradation when a blob's hard-link limit is reached" requirement, which of the two paths executes for a given mirror path can differ from one run to the next (a hardlink attempt can fail on one run, e.g. due to hitting the cap, and succeed on a later run once other paths referencing that blob are removed).

## Goals / Non-Goals

**Goals:**
- Mark a mirror entry read-only exactly when it is produced by the hardlink path, so an in-place naive overwrite fails at the OS level instead of silently mutating the shared blob.
- Keep a copy-fallback mirror entry writable, since it carries no shared-corruption risk.
- Keep the read-only attribute correct across runs even when a given mirror path's placement kind flips between hardlink and copy-fallback.
- Do not disturb `MoveMirrorEntry` or `RemoveFromMirror`, which must keep working on read-only entries exactly as they do today on writable ones.

**Non-Goals:**
- Detecting or reporting after the fact that a read-only mirror file was tampered with (e.g. via `attrib -r` followed by an edit) - out of scope; the read-only attribute is a friction/signal mechanism, not an enforced security boundary, and a determined user can always clear it.
- Any change to `ExtractTo` (single-file restore-to-arbitrary-location) or to how a migrated/copied mirror is made writable again on a new machine - those already fall outside `PlaceAtMirrorPath`, and clearing the attribute recursively is a standard Windows Explorer/`attrib` operation the user performs deliberately, not something Vara needs to automate.
- Cross-platform semantics beyond what .NET's `FileAttributes.ReadOnly` / `File.SetAttributes` already provide on Windows (Vara targets Windows per README).

## Decisions

**Set the attribute on the staged file before the atomic rename, not on the final mirror path after it.** `CreateHardLink`/`CopyWithProgress` both write to `stagingPath` first; `File.Move(stagingPath, mirrorPath, overwrite: true)` then does the atomic swap. Setting `FileAttributes.ReadOnly` on `stagingPath` immediately after it's populated (hardlink created, or copy completed) keeps the attribute change inside the same "prepare, then atomically swap" structure the method already uses for crash-safety, rather than adding a second, separate filesystem call after the rename that could itself be interrupted, leaving a hardlinked mirror file transiently writable. `File.Move` with `overwrite: true` preserves the moved file's own attributes (it replaces the destination rather than merging attributes with it), so the staged file's read-only bit carries through to the final mirror path.

**Always set the attribute explicitly for both branches, never rely on a prior run's leftover state.** Because `stagingPath` is a fresh temp file for every single call (`Path.Combine(_tempRoot, Guid.NewGuid().ToString("N"))`), there's no "leftover" attribute to inherit from a previous run on the *staged* file - but the *destination* mirror path can already exist read-only from a prior hardlink placement. Since the final step is `File.Move(..., overwrite: true)`, and the moved-in file's own attribute (set on `stagingPath` per this change) is what ends up at `mirrorPath`, explicitly setting `stagingPath`'s attribute on every call (read-only on the hardlink branch, explicitly *not* read-only / default on the copy branch) is sufficient to make the destination correct regardless of what it was before - no separate "clear stale read-only" step is needed against `mirrorPath` itself.

**Do not memoize placement-kind-to-attribute mapping across calls.** Consistent with the existing per-call (not per-blob) hardlink-vs-copy decision already documented in the method (a blob's link count isn't a stable fact), the read-only decision is likewise made fresh on every `PlaceAtMirrorPath` call from that call's own actual placement path, not cached or inferred from a prior call for the same mirror path or blob.

**Scope: only `PlaceAtMirrorPath`'s two internal branches, no new parameter or public API change.** `IContentStore.PlaceAtMirrorPath`'s signature is unchanged - callers (`BackupExecutor`) don't need to know or decide which placement kind occurred; that decision and its attribute consequence stay fully internal to `FileSystemContentStore`, mirroring how the hardlink-vs-copy fallback decision itself is already invisible to `BackupExecutor` today.

## Risks / Trade-offs

- [A hardlinked mirror is now read-only almost everywhere on a typical NTFS target (every Add/Change placement hardlinks when `SupportsHardlinks` is true), so restoring/migrating the mirror to a new machine and treating it as a live working copy requires an explicit step to clear the attribute] → Accepted trade-off (see conversation/proposal): this is the intended signal that a backup mirror is not meant to be edited in place, and Windows Explorer's folder-level Properties dialog (or `attrib -R /S /D`) already provides a single, familiar, one-step recursive fix for a user who deliberately wants to convert a copy into an editable working tree. Not solved by this change; documented as expected behavior.
- [`File.Move(..., overwrite: true)` semantics for attribute carry-over on Windows are relied upon rather than independently re-verified per call] → Mitigated by test coverage (see tasks.md) asserting the final mirror file's actual `FileAttributes.ReadOnly` state after each placement path, rather than only asserting the staged file's attribute before the move.
- [A future change to `MoveMirrorEntry`/`RemoveFromMirror` could inadvertently assume mirror entries are always writable] → Mitigated by adding explicit test coverage that both operations still succeed against a read-only hardlinked entry, so a regression here is caught by the existing test suite rather than only surfacing as a field report.
