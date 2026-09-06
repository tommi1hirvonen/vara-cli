## Context

`BackupDiffer.Diff` currently skips symlink/junction entries entirely (`if (entry.IsLink) continue;`)
before adding them to `scannedPaths`. `scannedPaths` is the only mechanism the diff uses to decide
"was this manifest-tracked path re-observed this run?" - anything absent from it that's also absent
from `failures` is classified deleted. See proposal.md for the user-facing motivation.

`BackupPlanner` currently only knows about `PendingChangeKind.Added`/`Changed` (from the diff) plus
its own move detection; there is no existing "this path is a link" pending-change kind, and the
manifest schema's `change_kind` column (see `FileChangeKind`) does not currently have a value for it
either - "recorded" today, per the original spec wording, only ever meant "not classified as deleted",
not "written as its own manifest row".

## Goals / Non-Goals

**Goals:**
- Stop symlinks from being able to cause false-deletion of previously-tracked content.
- Make "a path is a link" an observable, queryable manifest fact instead of an implicit absence.

**Non-Goals:**
- Following/traversing symlink targets, or backing up their content - explicitly out of scope,
  unchanged by this fix.
- Restoring a symlink's own metadata/target as a first-class restorable entity - out of scope for
  this fix; only the false-deletion bug and the "recorded" wording gap are addressed.

## Decisions

- **Add symlinks to `scannedPaths` before the early-continue.** This is the minimal change that
  fixes the false-deletion bug: `scannedPaths.Add(entry.RelativePath)` moves above the `IsLink`
  check (or the check is reordered), so a linked path is always considered "seen" for the run,
  regardless of whether it's otherwise diffed as content.
  - Alternative considered: track links in a separate `HashSet` and union it with `scannedPaths`
    only when computing deletions. Rejected as unnecessary indirection - `scannedPaths` already
    means exactly "seen this run", which is what a link is.
- **Introduce `FileChangeKind.Linked` (or equivalent) as a new manifest change kind**, written via a
  new `PendingChangeKind` the planner recognizes, so a link's presence (and a file-to-link
  transition) is an explicit, queryable manifest row rather than only "absent from the deleted
  set". This directly satisfies the "actually recorded" half of the proposal.
  - Alternative considered: don't add a new manifest row at all, and rely purely on the scan
    result's own transient `IsLink` flag each run. Rejected because it provides no history - a
    file-to-link transition would leave no trace of when it happened, and `GetFileHistory` couldn't
    show it.
- **A file-to-link transition is recorded as a new version row for the same path**, using the new
  `Linked` change kind, rather than being modeled as a delete-then-add pair. This keeps the path's
  history contiguous (no synthetic deletion event) and matches how `Changed` is already modeled for
  ordinary content changes.

## Risks / Trade-offs

- [Adding a new `FileChangeKind` touches the manifest schema's enum and every switch/pattern match
  over it] → Mitigated by grep-driven review of all `FileChangeKind` usages as an explicit task;
  the enum change itself needs no schema migration since it's stored as a string
  (`nameof(FileChangeKind...)`, per existing `SqliteSnapshotRepository` usage).
- [A previously-deleted path that reappears as a symlink is a new edge case (delete -> link, not
  file -> link)] → Covered by treating "currently a link" as just another state a path can be in,
  symmetric with how "currently deleted" already works, rather than special-casing the prior state.

## Open Questions

(none - the remaining detail, such as the exact presentation of a linked entry in `browse`/`history`
output, is a display concern deferred to normal implementation without affecting this change's
scope.)
