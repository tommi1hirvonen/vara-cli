## ADDED Requirements

### Requirement: Restore progress indication for large files
While a restore command is extracting a historical file's content to its destination, the system SHALL display a byte-based progress indicator reflecting bytes copied against the version's known total size, per the `progress-reporting` capability's incremental-progress requirement, so a restore of a large file does not appear to hang with no feedback. Because a version's total size is known before extraction begins, no separate scan or indeterminate phase is needed.

#### Scenario: Restoring a large file
- **WHEN** a user restores a historical version of a file large enough that its extraction takes a perceptible amount of time
- **THEN** the system displays a progress indicator that advances as the file's content is copied to the destination, starting immediately with the version's known total size rather than an indeterminate indicator

#### Scenario: Restoring a small file
- **WHEN** a user restores a historical version of a file small enough that its extraction completes almost immediately
- **THEN** the system completes the restore without the progress indicator producing a noticeable or distracting flash on screen

#### Scenario: Non-interactive or redirected output
- **WHEN** a restore command runs with output redirected or otherwise unable to render a live progress bar
- **THEN** the system falls back to plain, non-progress-bar output, per the `cli-presentation` capability's non-interactive fallback behavior
