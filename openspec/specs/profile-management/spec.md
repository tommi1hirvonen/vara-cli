# profile-management Specification

## Purpose

Defines the interactive `vara profiles` terminal experience for creating, editing, and deleting
backup profiles - including how a profile draft is validated and persisted - so users no longer
need to hand-edit `~/.vara/profiles.yml`.

## Requirements

### Requirement: Interactive profiles command
The system SHALL provide a `vara profiles` command, taking no required arguments, that opens an
interactive terminal UI for managing the profiles defined in `~/.vara/profiles.yml` (or the
loader's configured alternate path). WHEN standard input or standard output is not an interactive
terminal, the system SHALL report a clear error and take no action, rather than attempting to
render prompts a non-interactive session cannot answer.

#### Scenario: Command run in an interactive terminal
- **WHEN** a user runs `vara profiles` from an interactive terminal
- **THEN** the system displays the main profiles menu

#### Scenario: Command run with input or output redirected
- **WHEN** a user runs `vara profiles` with standard input or standard output redirected (not an
  interactive terminal)
- **THEN** the system reports an error stating that the command requires an interactive terminal,
  and makes no change to the configuration file

#### Scenario: Configuration file does not yet exist
- **WHEN** a user runs `vara profiles` and the configuration file does not exist
- **THEN** the system displays the main menu with zero profiles listed, and offers "Add new
  profile", rather than reporting a missing-file error

### Requirement: Main menu lists profiles and top-level actions
The main menu SHALL list every currently-saved profile by name, target root, and source count,
and SHALL offer "Add new profile" and "Quit" alongside the listed profiles. Selecting an existing
profile SHALL open that profile directly in the profile edit screen (populated with its current
saved values as the initial draft); no intermediate per-profile submenu SHALL be shown. Quitting
from the main menu SHALL exit the command making no further change to the configuration file.

#### Scenario: Profiles listed with summary information
- **WHEN** the configuration file defines one or more profiles
- **THEN** the main menu lists each profile's name, target root, and number of configured sources

#### Scenario: Selecting an existing profile opens it for editing
- **WHEN** a user selects an existing profile from the main menu
- **THEN** the system opens the profile edit screen with a draft initialized from that profile's
  currently-saved values, without showing any intermediate submenu

#### Scenario: Selecting "Add new profile"
- **WHEN** a user selects "Add new profile" from the main menu
- **THEN** the system opens the profile edit screen with an empty draft (no name, no target, no
  sources, no retention policy, no concurrency settings)

#### Scenario: Quitting the main menu
- **WHEN** a user selects "Quit" from the main menu
- **THEN** the system exits without making any further change to the configuration file

### Requirement: Deleting a profile requires explicit confirmation
The main menu SHALL offer a delete action for each listed profile. Choosing to delete a profile
SHALL prompt for an explicit yes/no confirmation, identifying the profile by name and target root,
before removing it. Deleting a profile SHALL only remove its entry from the configuration file; it
SHALL NOT delete, move, or otherwise modify any backup data previously written to that profile's
target root.

#### Scenario: Confirming deletion removes the profile
- **WHEN** a user chooses to delete a profile and confirms the prompt
- **THEN** the system removes that profile from the configuration file and returns to the main
  menu, which no longer lists it

#### Scenario: Declining deletion makes no change
- **WHEN** a user chooses to delete a profile and declines the confirmation prompt
- **THEN** the system makes no change to the configuration file and returns to the main menu with
  the profile still listed

#### Scenario: Deleting a profile does not touch backup data
- **WHEN** a user deletes a profile that has existing backup data under its target root
- **THEN** the system removes only the profile's entry from the configuration file, and the target
  root's existing contents are left unchanged

### Requirement: Profile fields are editable in any order
The profile edit screen SHALL let a user edit the profile's name, target root, sources, retention
policy, and concurrency settings in any order, from a screen that persists across edits to any of
those fields until the user saves or discards. The screen SHALL display the draft's current values
at all times, including fields not yet set.

#### Scenario: Editing fields in a non-sequential order
- **WHEN** a user is editing a new profile draft and sets the target root before setting the name
- **THEN** the system accepts the target root and continues to display the edit screen awaiting
  further edits, without requiring the name to be set first

#### Scenario: Unset fields are visibly distinguished
- **WHEN** a new profile draft has not yet had its name, retention policy, or concurrency settings
  set
- **THEN** the edit screen displays those fields as not set, distinct from a field that has an
  explicit value

### Requirement: Draft changes are validated live, after every field edit
WHEN a field of a profile draft (including a field of one of its sources) is changed, the system
SHALL immediately re-validate the draft using the same structural rules the configuration loader
already enforces (required fields, absolute paths, non-overlapping target/sources, non-negative
retention counts, positive concurrency values), plus a check that no other saved profile shares
the draft's name. WHEN this validation fails, the edit screen SHALL display the resulting error
alongside the draft's current values, rather than only surfacing it when the user attempts to
save.

#### Scenario: Invalid field value rejected immediately
- **WHEN** a user sets a source's path to a relative path
- **THEN** the system displays a validation error identifying the problem immediately, without
  requiring the user to attempt a save first

#### Scenario: Cross-field overlap detected live
- **WHEN** a user's draft already has a source configured, and the user then sets the target root
  to a path that overlaps that source
- **THEN** the system displays an overlap validation error immediately after the target root is
  set

#### Scenario: Duplicate name against another saved profile detected live
- **WHEN** a user sets a draft's name to match the name of a different, already-saved profile
- **THEN** the system displays a duplicate-name validation error immediately, identifying the
  conflicting profile

#### Scenario: Renaming a profile to its own current name is not a conflict
- **WHEN** a user is editing an existing profile and sets its name to the same name it already
  has
- **THEN** the system does not report a duplicate-name error for that profile against itself

#### Scenario: Valid draft shows no error
- **WHEN** every field of the draft currently satisfies all validation rules
- **THEN** the edit screen displays no validation error

### Requirement: Save persists only a fully valid draft
The profile edit screen SHALL offer a "Save" action that is available regardless of the draft's
current validity. WHEN "Save" is chosen and the draft is fully valid, the system SHALL persist it:
the draft's profile SHALL be added to (for a new profile) or replace its prior version within (for
an edited profile) the full in-memory list of profiles, which SHALL then be written to the
configuration file, and the system SHALL return to the main menu reflecting the change. WHEN
"Save" is chosen while the draft is invalid, the system SHALL redisplay the current validation
error and SHALL make no change to the configuration file.

#### Scenario: Saving a valid new profile
- **WHEN** a user completes a new profile draft that satisfies every validation rule and chooses
  "Save"
- **THEN** the system adds the new profile to the configuration file and returns to the main menu,
  which now lists it

#### Scenario: Saving a valid edit to an existing profile
- **WHEN** a user changes one or more fields of an existing profile's draft such that the draft
  remains valid, and chooses "Save"
- **THEN** the system replaces that profile's prior configuration with the draft's values in the
  configuration file and returns to the main menu, which reflects the updated values

#### Scenario: Attempting to save an invalid draft
- **WHEN** a user chooses "Save" while the draft has an outstanding validation error
- **THEN** the system redisplays that validation error, remains on the edit screen, and makes no
  change to the configuration file

### Requirement: Discard abandons the draft without persisting
The profile edit screen SHALL offer a "Discard" action, available regardless of the draft's
current validity, that returns to the main menu without changing the configuration file. WHEN
discarding a draft for a new profile, no profile SHALL be added. WHEN discarding a draft for an
existing profile, the profile's previously-saved values SHALL remain unchanged, and any edits made
since opening the edit screen SHALL be abandoned.

#### Scenario: Discarding a new profile draft
- **WHEN** a user has entered some fields of a new profile draft and chooses "Discard"
- **THEN** the system returns to the main menu, and no new profile appears in the configuration
  file

#### Scenario: Discarding edits to an existing profile
- **WHEN** a user has changed one or more fields of an existing profile's draft and chooses
  "Discard"
- **THEN** the system returns to the main menu, and that profile's configuration file entry
  remains exactly as it was before the edit screen was opened

#### Scenario: Discarding at any point is safe regardless of validity
- **WHEN** a user chooses "Discard" while the draft currently has an outstanding validation error
- **THEN** the system discards the draft the same as if it were valid, and makes no change to the
  configuration file

### Requirement: Sources are managed as an editable sub-list within a profile draft
Within the profile edit screen, a draft's sources SHALL be presented as their own list, supporting
adding a new source, editing an existing source's fields (path, recursive flag, exclude list,
include-glob list, exclude-glob list), and removing a source. Adding or editing a source SHALL
follow the same any-order-editing and live-validation behavior as the containing profile draft:
each change to a source's fields immediately re-validates the whole containing profile draft (so
overlap and other cross-field checks that depend on the target root or other sources are
evaluated), and no change to a source is persisted to the configuration file until the containing
profile draft is saved.

#### Scenario: Adding a source to a draft
- **WHEN** a user adds a new source to a profile draft and sets its path
- **THEN** the system includes that source in the draft's source list and re-validates the whole
  draft, without writing to the configuration file until the draft is saved

#### Scenario: Editing an existing source's fields
- **WHEN** a user changes the recursive flag, exclude list, or glob patterns of a source already
  in the draft
- **THEN** the system updates that source within the draft, re-validates the whole draft, and does
  not write to the configuration file until the draft is saved

#### Scenario: Removing a source from a draft
- **WHEN** a user removes a source from a profile draft that has more than one source
- **THEN** the system removes it from the draft's source list and re-validates the remaining draft

#### Scenario: A source's own overlap re-evaluates against the current target
- **WHEN** a profile draft's target root is set after a source has already been added to the
  draft
- **THEN** the system re-validates that source's path against the newly-set target root as part
  of the draft's live validation

### Requirement: Saving regenerates the whole configuration file without preserving prior formatting
WHEN a profile draft is saved, the system SHALL regenerate the entire contents of the
configuration file from the current in-memory list of profiles, rather than only rewriting the
portion of the file that changed. Comments, key ordering, or other formatting present in the
configuration file before the save SHALL NOT be required to survive the save; only the set of
profiles and their field values SHALL be preserved. The write SHALL be atomic - performed via a
temporary file that is then moved into place - so that an interruption during the write does not
leave the configuration file partially written or corrupted.

#### Scenario: Saving a change discards unrelated hand-authored comments
- **WHEN** the configuration file contained comments before a profile draft is saved (for example,
  explaining a field's purpose), and the save proceeds
- **THEN** the resulting file reflects the current set of profiles and their values, and the
  system does not attempt to preserve the prior comments

#### Scenario: Interrupted write does not corrupt the configuration file
- **WHEN** the process is interrupted while a save is writing the configuration file
- **THEN** the configuration file on disk is either its state from before the save or, if the
  write had already completed and been moved into place, its state from after the save - never a
  truncated or partially-written file
