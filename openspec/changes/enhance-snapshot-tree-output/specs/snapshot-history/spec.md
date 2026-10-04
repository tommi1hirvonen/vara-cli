## ADDED Requirements

### Requirement: Snapshot change rollups use change-kind colors
The system SHALL render each directory's added, changed, moved, deleted, and linked rollup indicator and count using the same change-kind color mapping used by snapshot file history. The indicator and count SHALL remain readable as plain text when color is unavailable.

#### Scenario: Distinguishing change kinds in directory rollups
- **WHEN** a snapshot change tree displays a directory with one or more change-kind counts
- **THEN** each kind's indicator and count use the established color for that kind, without applying one color to the entire directory label

#### Scenario: Reading rollups without color support
- **WHEN** a snapshot change tree is rendered where color is unavailable or disabled
- **THEN** the added, changed, moved, deleted, and linked indicators and their counts remain present as plain text

### Requirement: Snapshot change trees support an optional depth limit
The `vara snapshot` command SHALL accept an optional non-negative integer `--depth <n>` that limits the displayed directory tree to `n` directory levels below the selected scope root, with the scope root at depth zero. If omitted, the command SHALL display the full tree as before. A negative or non-integer depth value SHALL be rejected as invalid input.

Directory rollup counts SHALL continue to include all recorded changes beneath each directory, including descendants omitted from display by the depth limit. At each visible directory at the depth boundary, the report SHALL show the number of affected descendant directories omitted below that directory when that number is greater than zero. The omitted-directory indicator SHALL NOT replace or alter directory rollup counts.

When `--files` is also specified, file details SHALL be shown only under directories displayed within the selected depth, without changing how the depth limit is interpreted.

#### Scenario: Limiting displayed directory depth
- **WHEN** a user runs `vara snapshot <id> [directory] --depth 1`
- **THEN** the report displays the selected scope root and its immediate affected subdirectories, but no deeper affected directories

#### Scenario: Showing only the selected scope root
- **WHEN** a user runs `vara snapshot <id> [directory] --depth 0`
- **THEN** the report displays the selected scope root and omits all affected descendant directories

#### Scenario: Indicating omitted affected directories
- **WHEN** the depth limit hides affected descendant directories beneath a visible boundary directory
- **THEN** the report shows the number of affected descendant directories omitted beneath that visible directory

#### Scenario: Preserving rollups across a depth limit
- **WHEN** a directory has changes in descendants omitted by the requested depth limit
- **THEN** its displayed rollup counts include those descendant changes

#### Scenario: Preserving unlimited depth by default
- **WHEN** a user runs `vara snapshot <id> [directory]` without `--depth`
- **THEN** the report displays all affected directory levels as before

#### Scenario: Combining depth and file details
- **WHEN** a user runs `vara snapshot <id> [directory] --depth <n> --files`
- **THEN** the report displays file details for visible directories only and does not display directories deeper than `<n>`

#### Scenario: Rejecting an invalid depth
- **WHEN** a user supplies a negative or non-integer value to `--depth`
- **THEN** the command rejects the value instead of rendering a snapshot tree
