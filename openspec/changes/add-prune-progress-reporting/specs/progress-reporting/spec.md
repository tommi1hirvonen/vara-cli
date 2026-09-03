## ADDED Requirements

### Requirement: Count-based progress indication for non-byte-transfer operations
WHEN an operation's work is measured in discrete items rather than bytes transferred (for example, deleting individual unreferenced blobs during pruning), the system SHALL display a count-based progress indicator (items completed out of a known total), distinct from the byte-based progress bar used for file transfers. WHEN the total item count is not yet known (for example, while an operation is still determining what work is eligible), the system SHALL display an indeterminate progress indicator instead, replaced by the count-based indicator once the total is known.

#### Scenario: Total item count known upfront
- **WHEN** an operation has determined the total number of discrete items it will process
- **THEN** the system displays a count-based progress indicator showing items completed against that known total, updating as each item completes

#### Scenario: Total item count not yet known
- **WHEN** an operation is still determining which items are eligible for processing, before a total count is known
- **THEN** the system displays an indeterminate progress indicator, replaced by the count-based indicator once the total becomes known

#### Scenario: Non-interactive or redirected output
- **WHEN** an operation with count-based progress runs with output redirected or otherwise unable to render a live progress indicator
- **THEN** the system falls back to plain, non-progress-bar output, per the `cli-presentation` capability's non-interactive fallback behavior
