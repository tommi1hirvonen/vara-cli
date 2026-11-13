## ADDED Requirements

### Requirement: Interactive snapshot change tree
The system SHALL accept an `--interactive` option on `vara snapshot <id> [directory]` that presents the snapshot's change tree for keyboard navigation instead of printing it once. The interactive tree SHALL show the same information as the static report: the snapshot identity, status and totals, the directory tree scoped as for the static report, per-directory rollup counts in the change-kind colors, and the configured-source marks. `--interactive` SHALL imply the tree view and SHALL be combinable with `--files` and `--depth`. It SHALL NOT modify or create profile backing storage, and it SHALL NOT change the output of the command without `--interactive`.

The user SHALL be able to move the cursor between visible rows with the Up and Down keys. The Right key SHALL expand a collapsed directory that has children or files, and SHALL move the cursor to the first child of an already expanded directory. The Left key SHALL collapse an expanded directory, and SHALL move the cursor to the parent directory when the cursor is on a collapsed directory, a file row, or the scope root. The Home and End keys SHALL move the cursor to the first and last visible row. The `*` key SHALL expand every directory. The `q` and Esc keys SHALL end the session. Keys with no assigned meaning SHALL be ignored.

Every directory row SHALL show the rollup counts of its whole subtree, whether the directory is expanded or collapsed. Expanding or collapsing a directory SHALL NOT change any displayed count. A directory that has children or files SHALL carry a visible marker of its expanded or collapsed state.

The initial expansion SHALL follow `--depth <n>`: directories at or beyond `n` levels below the scope root start collapsed, and the scope root is at depth zero. When `--depth` is omitted, the interactive tree SHALL start with the scope root expanded and every other directory collapsed. When `--files` is given, changed files SHALL appear as non-expandable rows beneath their directory when it is expanded, with their change kind and, for a moved entry, its previous path.

If the snapshot id is unknown, or no changes are recorded for the requested scope, the command SHALL report that condition as the static report does, and SHALL NOT enter the interactive view. An invalid `--depth` SHALL be rejected as for the static report.

#### Scenario: Navigating with the arrow keys
- **WHEN** the interactive tree is shown and the user presses Down twice and then Up once
- **THEN** the cursor is on the second visible row

#### Scenario: Expanding a collapsed directory
- **WHEN** the cursor is on a collapsed directory that has affected subdirectories and the user presses Right
- **THEN** the directory is marked expanded and its immediate affected subdirectories appear beneath it, collapsed

#### Scenario: Moving into an expanded directory
- **WHEN** the cursor is on an expanded directory and the user presses Right
- **THEN** the cursor moves to the directory's first child row and no directory changes state

#### Scenario: Collapsing a directory
- **WHEN** the cursor is on an expanded directory and the user presses Left
- **THEN** the directory is marked collapsed and its descendants are no longer shown

#### Scenario: Moving to the parent
- **WHEN** the cursor is on a collapsed directory, a file row, or a leaf directory and the user presses Left
- **THEN** the cursor moves to the parent directory row and no directory changes state

#### Scenario: Left on the scope root
- **WHEN** the cursor is on the scope root and the user presses Left while it is collapsed
- **THEN** the cursor stays on the scope root

#### Scenario: Counts are the same collapsed or expanded
- **WHEN** a directory with affected descendants is collapsed and then expanded
- **THEN** the counts displayed on that directory row are identical in both states and include every recorded change beneath it

#### Scenario: Initial expansion from depth
- **WHEN** a user runs `vara snapshot <id> --interactive --depth 2`
- **THEN** the scope root and the directories one level below it start expanded, the directories two levels below start collapsed, and no directory deeper than that is visible until expanded

#### Scenario: Default initial expansion
- **WHEN** a user runs `vara snapshot <id> --interactive` without `--depth`
- **THEN** the scope root starts expanded with its immediate affected subdirectories visible and collapsed

#### Scenario: Expanding everything
- **WHEN** the user presses `*`
- **THEN** every directory with children or files is expanded

#### Scenario: Jumping to the ends
- **WHEN** the user presses End and then Home
- **THEN** the cursor moves to the last visible row and then to the first visible row

#### Scenario: Showing files in the interactive tree
- **WHEN** a user runs `vara snapshot <id> --interactive --files` and expands a directory with changes recorded directly in it
- **THEN** each changed file appears as a non-expandable row beneath the directory with its change kind, and a moved file also shows its previous path

#### Scenario: Ending the session
- **WHEN** the user presses `q` or Esc
- **THEN** the interactive view ends, the command exits with the success outcome, and the profile's backing storage is unchanged

#### Scenario: Unassigned keys
- **WHEN** the user presses a key with no assigned meaning
- **THEN** the view and cursor are unchanged

#### Scenario: Unknown snapshot or empty scope
- **WHEN** a user runs `vara snapshot <id> --interactive` for an unknown snapshot id or a scope with no recorded changes
- **THEN** the command reports that condition as the static report does and does not enter the interactive view

### Requirement: Interactive tree scrolls within the terminal
The interactive snapshot change tree SHALL display no more rows than fit in the terminal's height, together with the snapshot header. When the cursor moves beyond the displayed rows, the visible window SHALL scroll so the cursor row stays visible. The view SHALL show whether rows exist above or below the window. When the terminal is resized during the session, the view SHALL redraw to the new size with the cursor row still visible. A terminal too small to show the header and one row SHALL still show the cursor row.

#### Scenario: Cursor moves past the last visible row
- **WHEN** the tree has more rows than fit in the terminal and the user moves the cursor below the last displayed row
- **THEN** the window scrolls by enough rows to show the cursor row and the header remains in place

#### Scenario: Indicating hidden rows
- **WHEN** rows exist above or below the visible window
- **THEN** the view indicates that more rows are hidden in that direction

#### Scenario: Resizing the terminal
- **WHEN** the terminal height shrinks while the cursor is on a row that would no longer be shown
- **THEN** the view redraws with the cursor row visible

#### Scenario: Collapsing near the end of the list
- **WHEN** the window is scrolled to the end and the user collapses a directory, shortening the list
- **THEN** the window moves up as needed so it does not show empty space while rows are hidden above

### Requirement: Interactive tree falls back to the static report
When `--interactive` is given on `vara snapshot` and the session cannot be driven by keys, the system SHALL print the static change tree, with `--depth` and `--files` honored, and SHALL write a single line to standard error stating that the interactive view is unavailable. A session cannot be driven by keys when standard input or standard output is redirected, when the terminal does not support live in-place rendering, or when color is disabled. The fallback SHALL NOT change the exit outcome the static report would have.

#### Scenario: Output is redirected
- **WHEN** a user runs `vara snapshot <id> --interactive` with standard output redirected to a file
- **THEN** the static tree is written to the file, one line on standard error states that the interactive view is unavailable, and the exit outcome matches the static report

#### Scenario: Input is redirected
- **WHEN** a user runs `vara snapshot <id> --interactive` with standard input redirected
- **THEN** the static tree is printed and one line on standard error states that the interactive view is unavailable

#### Scenario: No color support
- **WHEN** a user runs `vara snapshot <id> --interactive` with `NO_COLOR` set
- **THEN** the static tree is printed in plain text and one line on standard error states that the interactive view is unavailable
