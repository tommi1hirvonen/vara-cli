## MODIFIED Requirements

### Requirement: Interactive profiles command
The system SHALL provide a `vara profiles` command, taking no required arguments, that opens an
interactive terminal UI for managing the profiles defined in `~/.vara/profiles.yml` (or the
loader's configured alternate path). WHEN running in an interactive terminal, the system SHALL
execute the interactive UI within the terminal's alternate screen buffer, switching to the alternate
screen buffer upon start and restoring the terminal's primary screen buffer upon exit (whether exiting
normally via Quit/Save/Discard, on interrupt via cancellation/Ctrl+C, or on error). Restoring the
primary screen buffer SHALL leave the user's prior terminal command history and scrollback untouched.
WHEN standard input or standard output is not an interactive terminal, the system SHALL report a clear
error and take no action, rather than attempting to render prompts a non-interactive session cannot answer.

#### Scenario: Command run in an interactive terminal switches to alternate screen buffer
- **WHEN** a user runs `vara profiles` from an interactive terminal
- **THEN** the system switches to the terminal's alternate screen buffer and displays the main profiles menu

#### Scenario: Command termination restores the primary screen buffer
- **WHEN** a user exits `vara profiles` by quitting, completing a save, discarding, or sending a cancellation signal (Ctrl+C)
- **THEN** the system restores the terminal's primary screen buffer with prior terminal history and scrollback intact

#### Scenario: Command run with input or output redirected
- **WHEN** a user runs `vara profiles` with standard input or standard output redirected (not an
  interactive terminal)
- **THEN** the system reports an error stating that the command requires an interactive terminal,
  and makes no change to the configuration file

#### Scenario: Configuration file does not yet exist
- **WHEN** a user runs `vara profiles` and the configuration file does not exist
- **THEN** the system displays the main menu with zero profiles listed, and offers "Add new
  profile", rather than reporting a missing-file error

### Requirement: Profile fields are editable in any order
The profile edit screen SHALL let a user edit the profile's name, target root, sources, retention
policy, and concurrency settings in any order, from a screen that persists across edits to any of
those fields until the user saves or discards. The screen SHALL display the draft's current values
at all times, including fields not yet set. Each time any screen in the profiles UI (re-)displays
its contents - including the main menu, the profile edit screen, the sources list screen, and individual
source edit screens - the system SHALL clear the alternate screen so that no earlier copy of a menu,
prompt, or draft summary remains visible on screen.

#### Scenario: Editing fields in a non-sequential order
- **WHEN** a user is editing a new profile draft and sets the target root before setting the name
- **THEN** the system accepts the target root and continues to display the edit screen awaiting
  further edits, without requiring the name to be set first

#### Scenario: Unset fields are visibly distinguished
- **WHEN** a new profile draft has not yet had its name, retention policy, or concurrency settings
  set
- **THEN** the edit screen displays those fields as not set, distinct from a field that has an
  explicit value

#### Scenario: Screen re-display clears alternate screen without duplicate output
- **WHEN** a user edits a field and the edit screen redisplays the draft's current values
- **THEN** the alternate screen is cleared and displays only the current draft summary and menu,
  with no earlier copy left visible above or below it

#### Scenario: Managing sources does not leave stale output on screen
- **WHEN** a user navigates to manage sources, adds or edits multiple sources, and returns to the edit screen
- **THEN** each screen transition clears the display, leaving no leftover prompts or source summaries on screen
