## Why

Vara has no command to list, create, edit, or delete backup profiles - users must hand-edit
`~/.vara/profiles.yml` against the annotated sample in `docs/config-sample.yml`, which is
error-prone (typos, malformed YAML, invalid paths) and offers no feedback until a later command
fails against the broken file. An interactive, validated editor removes that whole class of
mistakes and gives new users a discoverable way to get started.

## What Changes

- Add a `vara profiles` command that opens an interactive terminal UI (built on Spectre.Console
  prompts already used elsewhere in the CLI) for managing profiles, with no other arguments.
- Main menu lists existing profiles (name, target, source count) plus "Add new profile" and
  "Quit"; selecting an existing profile opens it directly in an edit screen (no intermediate
  submenu).
- Add/edit share one "profile review/edit" screen: an in-memory draft of a single profile's
  fields (name, target, sources, optional retention, optional concurrency) that can be edited in
  any order, live-validated after every field change (including cross-field checks such as
  target/source overlap and duplicate profile names, which need the whole draft and the rest of
  the profile list to evaluate), with `Save` and `Discard` actions.
- `Save` is always available; pressing it while the draft is invalid re-displays the current
  validation error and writes nothing. `Discard` abandons the draft (a brand new profile is never
  written; an existing profile reverts to its last-saved values) and returns to the main menu
  without touching the file.
- Only a successful `Save` persists: the full profile list (with the draft's profile added or
  replacing its prior version) is regenerated into `~/.vara/profiles.yml` and written atomically
  (temp file + rename), replacing the file's previous content - hand-authored comments or
  formatting in the file are not preserved.
- Sources are edited as their own sub-list within the profile draft (add/edit/remove a source:
  path, recursive flag, exclude list, include/exclude globs), reusing the same live-validation
  and any-order-editing pattern.
- Deleting a profile from the main menu requires an explicit confirmation prompt before removing
  it from the file; this only edits `~/.vara/profiles.yml`, it does not touch any backup data
  already written to that profile's target.
- Adds a profile-configuration writer alongside the existing read-only loader, and a
  cross-profile uniqueness validator (duplicate name checking today only happens implicitly when
  the whole file is loaded) that the interactive editor's live validation and the writer's final
  save both use.
- Out of scope for this change: non-interactive `vara profiles add/edit/remove` subcommands with
  command-line flags. The interactive UI is the only supported editing surface for now; a
  scriptable flag-based surface may reuse the same underlying draft/write services later.

## Capabilities

### New Capabilities
- `profile-management`: interactive creation, editing, and deletion of backup profiles through a
  `vara profiles` terminal UI, including draft validation, save/discard semantics, and writing
  the regenerated configuration file.

### Modified Capabilities
(none - `profile-config` continues to define file location, structural validation, and load-time
behavior exactly as today; this change adds a new way to produce a valid file, and reuses those
same validation rules rather than changing them)

## Impact

- New: `Vara.Cli.Commands.ProfilesCommand` (registered in `Program.cs` alongside the existing
  commands) and supporting Spectre.Console-driven presentation code under `Vara.Cli`.
- New: an `IProfileConfigWriter` (or similar) in `Vara.Infrastructure.Configuration`, mirroring
  `YamlProfileConfigLoader`, that regenerates `profiles.yml` from an in-memory profile list using
  the same AOT-safe YamlDotNet DOM approach (no reflection-based serialization).
- New: an application-level draft/editing service in `Vara.Application.Profiles` that holds a
  single profile's in-progress edits, re-validates on every change (including the cross-profile
  duplicate-name check), and drives Save/Discard.
- No changes to `Vara.Core.Configuration` domain types (`Profile`, `Source`, `RetentionPolicy`,
  `ConcurrencySettings`) or their existing validation rules; the new capability composes them
  as-is.
- No changes to any other command (`backup`, `restore`, `prune`, etc.) or to
  `~/.vara/profiles.yml`'s schema.
