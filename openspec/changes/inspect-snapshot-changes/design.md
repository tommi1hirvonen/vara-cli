## Context

See proposal.md for the motivation. The `snapshot-history` capability already exposes snapshot
records and file-version events, and the application layer already derives per-directory counts
for recursive-restore choices. The new report must handle large additions without making a
full changed-file listing the default. File-version rows distinguish Added, Changed, Moved,
Deleted, and Linked events and retain a previous path for a moved entry.

## Goals / Non-Goals

**Goals:**
- Derive directory summaries from the selected snapshot's recorded file-version events.
- Keep output concise for large snapshots while allowing opt-in changed-path detail.
- Reuse existing profile and directory path resolution conventions.

**Non-Goals:**
- Persisting per-directory statistics or changing the manifest schema.
- Comparing two snapshots or showing content diffs; existing history and diff commands remain
  responsible for those tasks.
- Reconstructing source configuration as it existed when an older snapshot was taken.

## Decisions

- Add a snapshot-specific repository query taking a snapshot id and normalized directory prefix.
  Filter by both in SQLite rather than loading every historical file-version row and filtering
  in memory; the existing all-history-under-prefix query is useful for restore history but is
  not a bounded query for a single snapshot.
- Aggregate each matching event into its containing directory and every ancestor up to the
  requested scope. Render only nodes that have events or affected descendants, using
  Spectre.Console's existing tree widget. Render five event-kind counts per directory. Keep a
  move at its recorded destination as a moved event and retain the old path for file detail;
  any separate deletion event at its former path remains represented under that path, consistent
  with the recorded event stream.
- Show a compact snapshot identity/status and existing aggregate stats above the tree, then the
  sparse directory rollups. Add changed-file leaves only when `--files` is set, preserving the
  same rollups. Use text nodes or escaped labels for paths so user-controlled path text is not
  interpreted as Spectre markup.
- Map the profile's currently configured source paths into mirror-relative paths using the
  existing absolute-path mirror mapping convention. Mark matching tree nodes as configured source
  roots. Historical source membership cannot be guaranteed after profile configuration changes,
  so labels describe the current configuration only.
- Resolve the optional directory using the same flexible directory resolution used by browsing.
  Keep the operation read-only and do not create missing profile storage while looking up a
  snapshot.

## Risks / Trade-offs

- A profile's configured sources may have changed since an old snapshot, so a tree node may no
  longer be identifiable as a source. The report marks only exact roots present in the current
  profile configuration and still displays all changed paths in the tree.
- A snapshot may contain many distinct changed directories even without file details. Sparse
  rendering avoids unchanged paths; a large snapshot can still produce a correspondingly large
  change tree because those directories represent real change locations.
- Move records can result in separate destination and old-path events. Counting the recorded
  event kinds at their recorded paths keeps the rollups consistent with the manifest and makes
  both affected locations visible.
