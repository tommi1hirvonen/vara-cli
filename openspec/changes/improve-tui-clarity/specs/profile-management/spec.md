## MODIFIED Requirements

### Requirement: Profile fields are editable in any order
The profile edit screen SHALL let a user edit the profile's name, target root, sources, retention
policy, and concurrency settings in any order, from a screen that persists across edits to any of
those fields until the user saves or discards. The screen SHALL display the draft's current values
at all times, including fields not yet set. Each time the edit screen (re-)displays the draft's
current values - including every time the user returns to it from a sub-screen or after editing a
field - the system SHALL first clear the terminal, so that no previously rendered copy of the
draft's summary remains on screen underneath the new one. WHEN the user leaves the edit screen
(via Save or Discard) back to the main profile menu, the system SHALL likewise clear the terminal
before displaying the main menu, so no previously rendered edit-screen content remains visible
underneath it.

#### Scenario: Editing fields in a non-sequential order
- **WHEN** a user is editing a new profile draft and sets the target root before setting the name
- **THEN** the system accepts the target root and continues to display the edit screen awaiting
  further edits, without requiring the name to be set first

#### Scenario: Unset fields are visibly distinguished
- **WHEN** a new profile draft has not yet had its name, retention policy, or concurrency settings
  set
- **THEN** the edit screen displays those fields as not set, distinct from a field that has an
  explicit value

#### Scenario: Re-displaying the edit screen does not duplicate prior output
- **WHEN** a user edits a field and the edit screen redisplays the draft's current values
- **THEN** the terminal shows only the current draft summary, with no earlier copy of it left
  visible above it

#### Scenario: Returning to a profile's edit screen after discarding does not duplicate prior output
- **WHEN** a user discards an edit session, selects a profile from the main menu again, and the
  edit screen opens for it
- **THEN** the terminal shows only that edit screen's current draft summary, with no leftover
  content from the earlier session still visible
