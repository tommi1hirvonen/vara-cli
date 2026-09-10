## ADDED Requirements

### Requirement: A failed configuration write is recoverable within the session
WHEN the system fails to write the configuration file while saving a profile draft or deleting a
profile, it SHALL report a clear error identifying the configuration file and the reason the write
failed, and SHALL keep the `vara profiles` session running on the screen the user was on, with the
in-progress draft and the in-memory profile list intact, so the user can retry the action, choose
a different one, or discard deliberately. The system SHALL NOT terminate the command, and SHALL
NOT present the failure as an unclassified internal failure with diagnostic output such as a stack
trace.

#### Scenario: Save fails because the configuration file cannot be written
- **WHEN** a user chooses "Save" for a valid draft and the configuration file cannot be written
  (for example, it is read-only, permission is denied, or the volume is full)
- **THEN** the system reports an error naming the configuration file and the reason, remains on
  the profile edit screen with the draft's values unchanged, and the configuration file's previous
  contents are left as they were

#### Scenario: Retrying a save after the cause of the failure is resolved
- **WHEN** a save has failed with a write error, the user resolves the underlying cause, and the
  user chooses "Save" again in the same session
- **THEN** the system writes the same draft successfully and returns to the main menu, without the
  user having had to re-enter any field

#### Scenario: Delete fails because the configuration file cannot be written
- **WHEN** a user confirms deletion of a profile and the configuration file cannot be written
- **THEN** the system reports an error naming the configuration file and the reason, returns to
  the main menu with the profile still listed, and the configuration file's previous contents are
  left as they were

#### Scenario: A write failure is not reported as an internal failure
- **WHEN** any configuration write in the `vara profiles` session fails
- **THEN** the system's output describes the failure in the same clear, user-facing error style
  used for other configuration errors, and contains no stack trace or unhandled-exception wording

### Requirement: Interrupting the profiles editor exits without persisting
The system SHALL let a user interrupt `vara profiles` at any prompt with the terminal's standard
interrupt (Ctrl+C) and exit promptly. An interrupt SHALL be treated as discarding any in-progress
draft: the system SHALL make no change to the configuration file as a result of the interrupt, and
any profile already saved earlier in the session SHALL remain saved.

#### Scenario: Interrupting while editing a draft
- **WHEN** a user presses Ctrl+C while a profile edit screen or any of its prompts is displayed
- **THEN** the command exits promptly, and the configuration file contains exactly what it
  contained before the draft was opened

#### Scenario: Interrupting after an earlier successful save
- **WHEN** a user saves one profile, then opens another draft and presses Ctrl+C
- **THEN** the command exits promptly, the earlier saved profile remains in the configuration
  file, and the abandoned draft is not written

#### Scenario: Interrupting at the main menu
- **WHEN** a user presses Ctrl+C at the main profiles menu
- **THEN** the command exits promptly and makes no change to the configuration file

### Requirement: A configuration write is durable before it replaces the previous file
WHEN the system writes the configuration file, it SHALL ensure the new contents have been
committed to durable storage before the new file replaces the previous one. As a result, after an
abrupt loss of the process or the machine at any point during a write, the configuration file
SHALL be readable as either its complete state from before the write or its complete state from
after the write.

#### Scenario: Abrupt loss of the machine during a write
- **WHEN** the machine loses power at any point while the system is writing the configuration file
- **THEN** on restart the configuration file is readable as either its complete previous state or
  its complete new state, and never as empty, truncated, or partially written content

#### Scenario: A completed save is not lost to a subsequent crash
- **WHEN** a save reports success and the process is then lost abruptly
- **THEN** the configuration file contains the saved profile list
