## ADDED Requirements

### Requirement: Key-driven views leave the terminal as they found it
A command that reads single key presses to drive a view SHALL repaint that view in place without leaving earlier frames in the scrollback, SHALL hide the text cursor while the view is active, and SHALL restore the cursor and leave the final screen in a clean state when the view ends. The restoration SHALL happen on normal exit, on an error, and on cancellation with Ctrl+C. Key presses SHALL NOT be echoed into the view. A key-driven view SHALL end with the success outcome when the user quits, and with the cancellation outcome used elsewhere when interrupted by Ctrl+C.

#### Scenario: Leaving the view normally
- **WHEN** a user quits a key-driven view
- **THEN** the cursor is visible again, no key presses have been echoed into the output, and the shell prompt appears on a clean line

#### Scenario: Interrupting the view with Ctrl+C
- **WHEN** a user presses Ctrl+C while a key-driven view is active
- **THEN** the cursor is restored and the command ends with the cancellation outcome

#### Scenario: Failure while the view is active
- **WHEN** an unclassified exception occurs while a key-driven view is active
- **THEN** the cursor is restored before the hard-error report is shown

### Requirement: Interactive tree markers remain readable without Unicode
The expand and collapse markers, the cursor indicator, and the hidden-rows indicators of a key-driven tree SHALL be composed of characters that remain legible on a console that cannot display Unicode, or SHALL be replaced by ASCII equivalents there, in the same way as tree guide characters. The cursor row SHALL be identifiable without relying on color alone.

#### Scenario: Console without Unicode support
- **WHEN** a key-driven tree is shown on a console that cannot display Unicode
- **THEN** guides, state markers, the cursor indicator and hidden-rows indicators are all shown in ASCII, and the cursor row is still distinguishable

#### Scenario: Cursor row without color
- **WHEN** the key-driven tree is shown where color is available but the user cannot tell colors apart
- **THEN** the cursor row is still marked by a non-color indicator
