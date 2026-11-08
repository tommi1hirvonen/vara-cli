## Why

`vara profiles` is the interactive editor for the profile configuration. It lists profiles, and adds, edits and deletes them through a draft that is validated live and saved only when valid. Saving regenerates the whole configuration file and writes it durably before it replaces the old one.

It is the last command to migrate. It reuses the profile model and validation from `fsharp-profile-config`, and adds the one thing that change left out: writing the configuration file.

Design and tasks for this change are written by `fsharp-retention-and-integrity`'s final task, using the findings of the changes before it.

## What Changes

- Add `vara profiles` to the F# `vara`:
  - the main menu;
  - profile fields editable in any order;
  - sources managed as a sub-list;
  - live validation of the draft after every edit;
  - save, discard, and delete with confirmation;
  - interruption without persisting.
- Model the draft as a pure state machine in `Vara.Domain`. Its live validation reuses the `fsharp-profile-config` validation, so the editor and the loader cannot disagree.
- Add a configuration writer to `Vara.Engine`. It regenerates the YAML, and writes durably: write a temporary file, flush it, then replace the old file. A failed write can be retried within the same session.
- As its last task, update `fsharp-cutover` with this change's findings.

There are no user-visible changes: the shipped `vara.exe` is still the C# build.

## Capabilities

### New Capabilities

None.

### Modified Capabilities

None. This is a behavior-preserving reimplementation (`skip_specs: true`).

**Owned scenarios:** every `profile-management` requirement.

## Impact

- **Code:** all three F# source projects grow. No changes to the C# projects.
- **Tests:** scenario tests driven through `Spectre.Console.Testing` input, and a parity test showing that a file saved by the F# editor loads the same in the C# loader, and the other way round.
- **Dependencies:** requires `fsharp-retention-and-integrity`, as the previous change in the series. Functionally it needs only `fsharp-profile-config` and the presentation conventions from `fsharp-backup-presentation`.
