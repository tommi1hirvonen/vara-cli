## ADDED Requirements

### Requirement: Restore task labels are bounded to a fixed width
While the `restore` command's live progress display is showing a single-file or recursive
directory restore's progress, the task label built from the restored path SHALL be bounded to a
fixed character budget before it is handed to the progress display, rather than passing the
path's full, untruncated text. WHEN the path exceeds that budget, the system SHALL shorten it by
dropping leading parent directory segments and prefixing the remainder with `...`, preserving as
much of the path's trailing segments (ending in the filename) as fit within the budget. WHEN even
the filename alone does not fit within the budget, the system SHALL instead truncate the filename
itself so the label still fits. This SHALL apply to both the single-file restore path and the
recursive directory restore path, so that in neither case can the label's raw text force the
progress bar below a usable, legible width.

#### Scenario: Restoring a file at a short path
- **WHEN** a single-file restore's resolved path fits within the task label's fixed budget
- **THEN** the system displays the path unmodified, and the progress bar renders at its normal
  width

#### Scenario: Restoring a file at a deeply nested path
- **WHEN** a single-file restore's resolved path exceeds the task label's fixed budget
- **THEN** the system displays a shortened form of the path that drops one or more leading parent
  directories, prefixed with `...`, retaining the filename and as many trailing parent segments as
  fit, and the progress bar still renders at its normal, unshrunk width

#### Scenario: Restoring a file whose name alone exceeds the budget
- **WHEN** a single-file restore's filename alone is longer than the task label's fixed budget
- **THEN** the system truncates the filename itself to fit the budget, rather than leaving the
  label untruncated or omitting it entirely

#### Scenario: Recursive directory restore at a deeply nested path
- **WHEN** a recursive directory restore's resolved directory path exceeds the task label's fixed
  budget
- **THEN** the system displays a shortened form of the directory path using the same truncation
  rule as the single-file case, and the progress bar still renders at its normal, unshrunk width
