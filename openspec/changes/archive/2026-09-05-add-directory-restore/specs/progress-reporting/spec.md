## ADDED Requirements

### Requirement: Aggregate progress across a multi-file directory restore
While a recursive directory restore (per the `snapshot-history` capability) is writing multiple
files, the system SHALL report progress as a single byte-based indicator against the combined
total size of every file to be written in that operation, per the existing byte-based and
upfront-total-known progress requirements, rather than showing a separate progress indicator per
file. Since every file's size is known from the manifest before the restore begins, no separate
scan phase is needed before this indicator can display a known total.

#### Scenario: Restoring a directory with many files
- **WHEN** a recursive directory restore is writing a set of files whose combined transfer takes
  a perceptible amount of time
- **THEN** the system displays one progress indicator that advances continuously as each file is
  written, reflecting bytes written so far against the combined total across all files, rather
  than resetting or restarting for each file

#### Scenario: Restoring a directory whose files are all small
- **WHEN** a recursive directory restore's combined content is small enough that the whole
  operation completes almost immediately
- **THEN** the system completes the restore without the progress indicator producing a noticeable
  or distracting flash on screen

#### Scenario: File removals during a directory restore do not distort progress
- **WHEN** a recursive directory restore both writes files and removes files that did not exist
  at the requested date
- **THEN** the removals, which transfer no bytes, are not counted toward the combined total or
  the bytes-written figure, so they do not distort the displayed percentage, throughput, or ETA

#### Scenario: Non-interactive or redirected output
- **WHEN** a recursive directory restore runs with output redirected or otherwise unable to
  render a live progress bar
- **THEN** the system falls back to plain, non-progress-bar output, per the `cli-presentation`
  capability's non-interactive fallback behavior
