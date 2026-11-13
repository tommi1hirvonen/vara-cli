## MODIFIED Requirements

### Requirement: Interactive snapshot change tree
The system SHALL accept an `--interactive` option on `vara snapshot <id> [directory]` that presents the snapshot's change tree for keyboard navigation instead of printing it once. The interactive tree SHALL show the same information as the static report: the snapshot identity, status and totals, the directory tree scoped as for the static report, per-directory rollup counts in the change-kind colors, and the configured-source marks. `--interactive` SHALL imply the tree view and SHALL be combinable with `--files` and `--depth`; `--files` SHALL have no further effect on the interactive tree, which lists changed files regardless. It SHALL NOT modify or create profile backing storage, and it SHALL NOT change the output of the command without `--interactive`.

The user SHALL be able to move the cursor between visible rows with the Up and Down keys. The Right key SHALL expand a collapsed directory that has children or visible file rows, and SHALL move the cursor to the first child of an already expanded directory. The Left key SHALL collapse an expanded directory, and SHALL move the cursor to the parent directory when the cursor is on a collapsed directory, a file row, or the scope root. The Home and End keys SHALL move the cursor to the first and last visible row. The `*` key SHALL expand every directory. The `q` and Esc keys SHALL end the session. Keys with no assigned meaning SHALL be ignored.

Every directory row SHALL show the rollup counts of its whole subtree, whether the directory is expanded or collapsed. Expanding or collapsing a directory SHALL NOT change any displayed count. A directory that has children or visible file rows SHALL carry a visible marker of its expanded or collapsed state.

The initial expansion SHALL follow `--depth <n>`: directories at or beyond `n` levels below the scope root start collapsed, and the scope root is at depth zero. When `--depth` is omitted, the interactive tree SHALL start with the scope root expanded and every other directory collapsed. The changed files of an expanded directory SHALL appear as non-expandable rows beneath it, as specified for the interactive file rows.

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
- **THEN** every directory with children or visible file rows is expanded

#### Scenario: Jumping to the ends
- **WHEN** the user presses End and then Home
- **THEN** the cursor moves to the last visible row and then to the first visible row

#### Scenario: Showing files in the interactive tree
- **WHEN** a user runs `vara snapshot <id> --interactive` without `--files` and expands a directory with changes recorded directly in it
- **THEN** each changed file appears as a non-expandable row beneath the directory

#### Scenario: Files option has no further effect
- **WHEN** a user runs `vara snapshot <id> --interactive --files`
- **THEN** the interactive tree behaves exactly as it does without `--files`

#### Scenario: Ending the session
- **WHEN** the user presses `q` or Esc
- **THEN** the interactive view ends, the command exits with the success outcome, and the profile's backing storage is unchanged

#### Scenario: Unassigned keys
- **WHEN** the user presses a key with no assigned meaning
- **THEN** the view and cursor are unchanged

#### Scenario: Unknown snapshot or empty scope
- **WHEN** a user runs `vara snapshot <id> --interactive` for an unknown snapshot id or a scope with no recorded changes
- **THEN** the command reports that condition as the static report does and does not enter the interactive view

## ADDED Requirements

### Requirement: Interactive file rows with change symbols
The interactive change tree SHALL list the changed files recorded directly in a directory as non-expandable rows beneath it when the directory is expanded, after its child directories and ordered by file name without regard to case, for every directory that holds a change, including the deepest ones. Each file row SHALL begin with a symbol for the file's change kind, in the change-kind color of that kind: `+` for added, `~` for changed, `->` for moved, `-` for deleted and `@` for linked, followed by the file name. The row under the cursor SHALL be described on a detail line that shows its full mirror path and, for a moved file, the path it was moved from. A directory row's detail line SHALL show its full mirror path.

#### Scenario: Files under the deepest directory
- **WHEN** the user expands a directory that has no affected subdirectories and holds changes recorded directly in it
- **THEN** its changed files appear beneath it, each with the symbol of its change kind and its name

#### Scenario: Symbols for each change kind
- **WHEN** an expanded directory holds one added, one changed, one moved, one deleted and one linked file
- **THEN** their rows begin with `+`, `~`, `->`, `-` and `@` respectively, each in the color of its change kind

#### Scenario: Files are ordered after directories
- **WHEN** an expanded directory has both affected subdirectories and changed files
- **THEN** the subdirectories are listed first and the files follow in name order

#### Scenario: Detail line for a moved file
- **WHEN** the cursor is on a moved file's row
- **THEN** the detail line shows the file's full mirror path and the path it was moved from

#### Scenario: Detail line for a directory
- **WHEN** the cursor is on a directory row
- **THEN** the detail line shows that directory's full mirror path

### Requirement: A moved file absorbs its source's deleted row
When the changes of the interactive tree include a moved entry and a deleted entry whose path is that moved entry's recorded previous path, the tree SHALL NOT list the deleted entry as a file row. The move SHALL appear as the one moved row at its new path. The deleted entry SHALL still be included in every directory count it was part of, and the counts SHALL be the same as in the static report. A deleted entry that is not the previous path of a moved entry SHALL be listed normally. When the cursor is on a directory row, the detail line SHALL state how many deleted entries in that directory's subtree are not listed because moves absorb them, when that number is greater than zero.

#### Scenario: Directory detail explains absorbed deletions
- **WHEN** the cursor is on a directory whose subtree contains deleted entries that moves absorb
- **THEN** the detail line states how many deleted entries are not listed because of moves

#### Scenario: A move reads as one row
- **WHEN** a snapshot recorded a file as moved from one directory to another, which also recorded a deleted entry at the old path
- **THEN** the interactive tree shows only the moved row, under the new directory, and no deleted row under the old directory

#### Scenario: Counts still include the absorbed deletion
- **WHEN** a directory's rollup counts include a deleted entry that the tree does not list because a move absorbed it
- **THEN** the directory's displayed deleted count is unchanged from the static report

#### Scenario: A plain deletion is listed
- **WHEN** a snapshot recorded a deleted file that is not the previous path of any moved file
- **THEN** the interactive tree lists it as a deleted row

### Requirement: Toggling deleted and file rows
The interactive change tree SHALL show deleted file rows and file rows by default. The `d` key SHALL toggle whether deleted file rows are shown, and the `f` key SHALL toggle whether any file rows are shown. Both toggles SHALL change only which file rows are listed: directory rows, their counts, the directory structure and the expansion state of directories SHALL NOT change. A directory with no child directories and no visible file rows SHALL be shown without an expand marker and SHALL NOT be expandable. The status line SHALL state whether deleted rows and file rows are shown and how many rows each toggle currently hides. When a toggle hides the row under the cursor, the cursor SHALL move to the nearest visible row above it that is an ancestor, so that it stays on a visible row.

#### Scenario: Deleted rows are shown by default
- **WHEN** the interactive tree opens on a snapshot that recorded deleted files
- **THEN** the deleted files are listed under their directories and the status line states that deleted rows are shown

#### Scenario: Hiding deleted rows
- **WHEN** the user presses `d` while deleted file rows are shown
- **THEN** the deleted file rows are no longer listed, the status line states that deleted rows are hidden and how many are hidden, and no directory count changes

#### Scenario: Showing deleted rows again
- **WHEN** the user presses `d` while deleted file rows are hidden
- **THEN** the deleted file rows are listed again and the status line states that they are shown

#### Scenario: Hiding all file rows
- **WHEN** the user presses `f` while file rows are shown
- **THEN** no file rows are listed, the status line states that file rows are hidden and how many are hidden, and the directory structure and counts are unchanged

#### Scenario: Directories with nothing visible to expand
- **WHEN** file rows are hidden and a directory has no affected subdirectories
- **THEN** the directory is shown without an expand marker and Right does not change it

#### Scenario: Cursor on a row that becomes hidden
- **WHEN** the cursor is on a deleted file row and the user presses `d`
- **THEN** the cursor moves to the file's directory row

#### Scenario: Expanding everything with file rows hidden
- **WHEN** file rows are hidden and the user presses `*`
- **THEN** every directory with child directories is expanded and no file rows appear
