## Why

Retention pruning keeps the target's history bounded, and the integrity check verifies that stored content still matches the manifest. Both are maintenance commands over the same manifest and content store.
- **Prune** is driven by a tiered retention policy: daily, weekly, monthly and yearly buckets. That is a rule-heavy, naturally pure computation, and it follows from the profile model built in `fsharp-profile-config`.
- **Check** is a bounded-parallel read workload, with count-based progress.

Design and tasks for this change are written by `fsharp-snapshot-history`'s final task, using the findings of the changes before it.

## What Changes

- Add `vara prune`:
  - a pure retention evaluation that decides which snapshots to keep and which to prune;
  - pruning of manifest rows that never removes a path's current state;
  - garbage collection of content that is no longer referenced;
  - the run lock, progress, and graceful cancellation.
- Add `vara check`: full and quick verification of stored content against the manifest, with a scriptable exit status, progress with an ETA, and graceful cancellation.
- Add the count-based progress display used by both commands.
- As its last task, update the following changes with this change's findings.

There are no user-visible changes: the shipped `vara.exe` is still the C# build.

## Capabilities

### New Capabilities

None.

### Modified Capabilities

None. This is a behavior-preserving reimplementation (`skip_specs: true`).

**Owned scenarios:**
- **`retention-pruning`:** every requirement.
- **`backup-integrity`:** every requirement.
- **`progress-reporting`:** "Count-based progress indication for non-byte-transfer operations".
- **`profile-config`:** "Profile without a retention policy".

## Impact

- **Code:** all three F# source projects grow. No changes to the C# projects.
- **Tests:** scenario tests, FsCheck properties for retention evaluation (for example, a snapshot kept by any tier is never pruned, and the pruned set does not depend on input order), and parity tests that run the C# and F# commands against the same targets and compare the remaining manifest and blobs.
- **Dependencies:** requires `fsharp-snapshot-history`, for the manifest queries and presentation it adds.
