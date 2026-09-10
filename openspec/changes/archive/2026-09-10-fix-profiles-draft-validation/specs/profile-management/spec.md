## MODIFIED Requirements

### Requirement: Draft changes are validated live, after every field edit
WHEN a profile draft changes in any way - a field of the profile, a field of one of its sources,
or the composition of its source list (a source added or removed) - the system SHALL immediately
re-validate the draft using the same structural rules the configuration loader already enforces
(required fields, absolute paths, non-overlapping target/sources, non-negative retention counts,
positive concurrency values), plus a check that no other saved profile shares the draft's name.
WHEN this validation fails, the edit screen SHALL display the resulting error alongside the
draft's current values, rather than only surfacing it when the user attempts to save. The validity
indication the edit screen displays SHALL always describe the draft's current state; it SHALL NOT
report a draft as valid on the basis of an earlier state that has since been edited.

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
- **THEN** the system displays a duplicate-name validation error immediately, naming the
  conflicting saved profile and describing the conflict in terms of the profile being edited

#### Scenario: Renaming a profile to its own current name is not a conflict
- **WHEN** a user is editing an existing profile and sets its name to the same name it already
  has
- **THEN** the system does not report a duplicate-name error for that profile against itself

#### Scenario: Valid draft shows no error
- **WHEN** every field of the draft currently satisfies all validation rules
- **THEN** the edit screen displays no validation error

#### Scenario: Adding an incomplete source immediately invalidates the draft
- **WHEN** a user adds a new source to an otherwise-valid draft and leaves the edit screen without
  giving that source a path
- **THEN** the edit screen displays a validation error for the incomplete source rather than
  reporting the draft as valid

#### Scenario: Numeric entry is interpreted independently of the machine's locale
- **WHEN** a user enters a whole number for a concurrency or retention field on a machine whose
  locale formats numbers differently from the configuration file's format
- **THEN** the system interprets the entered value the same way it would be written to and read
  back from the configuration file, with no locale-dependent difference

### Requirement: Save persists only a fully valid draft
The profile edit screen SHALL offer a "Save" action that is available regardless of the draft's
current validity. WHEN "Save" is chosen, the system SHALL validate the draft's current state at
that moment. WHEN that validation succeeds, the system SHALL persist exactly that state: the
profile the validation produced SHALL be added to (for a new profile) or replace its prior version
within (for an edited profile) the full in-memory list of profiles, which SHALL then be written to
the configuration file, and the system SHALL return to the main menu reflecting the change. The
system SHALL NOT persist any earlier, cached version of the draft. WHEN that validation fails, the
system SHALL display the resulting error, remain on the edit screen, and make no change to the
configuration file.

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

#### Scenario: Saved content matches the draft on screen
- **WHEN** a user chooses "Save" for a draft that is valid
- **THEN** the profile written to the configuration file contains every field value the edit
  screen currently displays, including every source currently listed on it

#### Scenario: Save refuses a draft that became invalid after its last displayed validation
- **WHEN** a draft was valid at some earlier point, has since been edited into an invalid state,
  and the user chooses "Save"
- **THEN** the system reports the draft's current validation error, remains on the edit screen,
  and makes no change to the configuration file - it does not fall back to writing the earlier
  valid state

### Requirement: Sources are managed as an editable sub-list within a profile draft
Within the profile edit screen, a draft's sources SHALL be presented as their own list, supporting
adding a new source, editing an existing source's fields (path, recursive flag, exclude list,
include-glob list, exclude-glob list), and removing a source. Adding or editing a source SHALL
follow the same any-order-editing and live-validation behavior as the containing profile draft:
adding a source and each change to a source's fields immediately re-validates the whole containing
profile draft (so overlap and other cross-field checks that depend on the target root or other
sources are evaluated), and no change to a source is persisted to the configuration file until the
containing profile draft is saved. A source SHALL become part of the draft - and count toward the
draft's validity - from the moment it is added, whether or not its fields have been filled in yet.
Once a draft has been validated into a profile, later edits to the draft's sources SHALL NOT
retroactively alter that already-validated profile.

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

#### Scenario: A newly added source is part of the draft before its fields are filled in
- **WHEN** a user adds a source to a draft and navigates back to the profile edit screen without
  filling in any of its fields
- **THEN** the edit screen still lists that source as part of the draft, and the draft's validity
  reflects it

#### Scenario: Later source edits do not change an already-validated profile
- **WHEN** a draft has been validated into a profile and the user afterwards edits one of the
  draft's source exclude or glob lists
- **THEN** the previously-validated profile retains the list contents it was validated with, and
  the edited lists only take effect through a subsequent validation of the draft
