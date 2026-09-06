## Why

The `backup-execution` spec's "Symlinks and junctions are not followed" requirement states a
symlink's existence is recorded without traversing it, and the README repeats this. In practice,
`BackupDiffer.Diff` hits `if (entry.IsLink) continue;` before `scannedPaths.Add(entry.RelativePath)`
runs, so a scanned symlink is never added to `scannedPaths` and therefore never recorded at all.
Worse, if a path was previously tracked as a regular file and later becomes a symlink, it now
disappears from `scannedPaths` entirely, so the "not re-observed this run" branch classifies the
prior file as deleted - an incorrect, potentially destructive misclassification of a live path.

## What Changes

- A scanned symlink/junction entry is added to the diff's "seen this run" tracking (or an
  equivalent mechanism) so it is never mistaken for a deletion, satisfying the existing "recorded,
  not traversed" requirement in practice, not just in wording.
- A path that transitions from a regular tracked file to a symlink between runs is recorded as a
  distinct, non-deleted state rather than silently vanishing from the manifest's current view.

## Capabilities

### New Capabilities

(none)

### Modified Capabilities

- `backup-execution`: the "Symlinks and junctions are not followed" requirement is clarified so
  that a symlink's existence is actually recorded (not just not-traversed), and a tracked file
  that becomes a symlink is never misclassified as deleted.

## Impact

- **Code**: `src/Vara.Application/Backup/BackupDiffer.cs` (`Diff`).
- **Tests**: `tests/Vara.Application.Tests/Backup/BackupDifferTests.cs` (or equivalent) covering a
  symlink present across a run, and a file that becomes a symlink between two runs.
- Potential downstream impact on `BackupPlanner`/manifest writes if symlink recording requires a
  new pending-change kind rather than reusing an existing one - to be resolved in design.md.
