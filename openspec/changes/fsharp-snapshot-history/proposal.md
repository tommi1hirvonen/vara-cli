## Why

Snapshot history is how users get their data back. It covers listing snapshots, a file's version history, restoring files and directories, showing and diffing versions, inspecting what a snapshot changed, and browsing a point in time. It is the largest capability, with 811 spec lines, and it reads the manifest and content store the engine writes.

Its decisions are mostly pure functions over manifest rows: which version was live at a given moment, how a move chain links a file's history, what a directory looked like, and what a restore must write or remove. That makes it the second large test of the functional design, after the engine.

Design and tasks for this change are written by `fsharp-backup-presentation`'s final task, using the findings of the changes before it.

## What Changes

- Add the history commands to the F# `vara`: `snapshots`, `snapshot`, `history`, `restore` (a file or a directory, at a version or a date, to the original location or elsewhere), `show`, `diff`, `browse` and `deleted`.
- Add to `Vara.Domain` the pure history model:
  - point-in-time state;
  - move-chain history;
  - directory listings with deleted entries;
  - the snapshot change roll-up;
  - restore planning.
- Add the manifest queries and restore effects to `Vara.Engine`, including atomic restore writes and the guard against writing into the live mirror. Restore uses the engine's read-only manifest mode.
- Wire profile resolution from the working directory, which `fsharp-profile-config` built, into the browsing and restore commands.
- Add date/time option parsing with friendly errors and an example value in the help text.
- As its last task, update the following changes with this change's findings.

There are no user-visible changes: the shipped `vara.exe` is still the C# build.

## Capabilities

### New Capabilities

None.

### Modified Capabilities

None. This is a behavior-preserving reimplementation (`skip_specs: true`).

**Owned scenarios:**
- **`snapshot-history`:** every requirement.
- **`backup-browsing`:** every requirement.
- **`cli-presentation`:** "Bordered widgets use a consistent rounded-corner style", "Invalid date/time option input produces a friendly error" and "Date/time options document an example value in their help text".
- **`progress-reporting`:** "Aggregate progress across a multi-file directory restore" and "Restore task labels are bounded to a fixed width".

## Impact

- **Code:** all three F# source projects grow, and DiffPlex is added to `Vara.Cli` (it is already pinned centrally). No changes to the C# projects.
- **Tests:** scenario tests for each owned requirement, and parity tests that run the C# and F# commands against the same targets and compare their output and restored files.
- **Dependencies:** requires `fsharp-backup-presentation`, for the shared presentation conventions and progress rendering.
