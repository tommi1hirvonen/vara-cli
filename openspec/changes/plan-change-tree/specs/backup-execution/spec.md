## ADDED Requirements

### Requirement: Change tree options on the backup command
The system SHALL accept the options `--depth <n>`, `--files` and `--interactive` on `vara backup <profile>`. Giving any of them SHALL select the change tree view, which is shown after the command's usual summary, and `--interactive` SHALL imply the tree. `--depth` SHALL accept a non-negative integer, and a negative or non-integer value SHALL be rejected as invalid input before any scan or run starts. The tree SHALL be the directory tree used by `vara snapshot`: directories rooted at the mirror root, each with the added, changed, moved, deleted and linked counts of its whole subtree in the change-kind colors, configured source roots marked as sources, with `--files` adding the changed file paths and their change kinds (and a moved entry's previous path). `--depth <n>` SHALL limit the displayed levels below the root as it does for `vara snapshot`, without changing any count. Without any of the three options, the command's output SHALL be unchanged.

`--json` SHALL NOT be combined with `--depth`, `--files` or `--interactive`: the combination SHALL be rejected as a usage error before any scan or run starts.

#### Scenario: Static tree after a dry run
- **WHEN** a user runs `vara backup <profile> --dry-run --depth 1`
- **THEN** the dry-run summary is shown, followed by the directory tree limited to the root and its immediate affected subdirectories, with counts that include all planned changes beneath each directory

#### Scenario: File paths in the tree
- **WHEN** a user runs `vara backup <profile> --dry-run --files`
- **THEN** the tree also lists each planned file under its directory with its change kind, and a planned move shows the path it moves from

#### Scenario: Unchanged output without tree options
- **WHEN** a user runs `vara backup <profile> --dry-run` or `vara backup <profile>` without `--depth`, `--files` or `--interactive`
- **THEN** the output is the same as before this requirement existed, and no tree is shown

#### Scenario: Rejecting an invalid depth
- **WHEN** a user supplies a negative or non-integer value to `--depth` on `vara backup`
- **THEN** the command rejects the value as invalid input and does not scan or run

#### Scenario: Rejecting a tree option with JSON
- **WHEN** a user runs `vara backup <profile> --json` together with `--depth`, `--files` or `--interactive`
- **THEN** the command reports a usage error and does not scan or run

### Requirement: Dry-run change tree shows the planned changes
When a tree option is given with `--dry-run`, the system SHALL build the tree from the planned changes of the same planning pass that produced the dry-run counts, so the totals of the tree equal the reported added, changed, moved and deleted counts, with planned links shown in the linked count. The system SHALL show the tree after planning has finished and after the run lock has been released. The dry run SHALL continue to perform no writes: no content, no manifest rows, no snapshot record and no manifest file created, whether the tree is static or interactive. A dry run with no planned changes SHALL report zero planned changes and show no tree.

#### Scenario: Tree totals match the dry-run counts
- **WHEN** a user runs `vara backup <profile> --dry-run --depth 0`
- **THEN** the root row's added, changed, moved and deleted counts equal the counts in the dry-run summary

#### Scenario: Dry-run tree writes nothing
- **WHEN** a user runs `vara backup <profile> --dry-run --interactive` and browses the tree
- **THEN** afterward the mirror, content store and manifest are unchanged and no new snapshot record exists

#### Scenario: No planned changes
- **WHEN** a user runs `vara backup <profile> --dry-run --files` for a profile with no changes since the last run
- **THEN** the system reports zero planned changes, shows no tree, and exits successfully

#### Scenario: Run lock is not held while browsing
- **WHEN** a user is browsing the interactive dry-run tree
- **THEN** the profile's run lock has already been released and another run for the same target can start

### Requirement: Real-run change tree shows the recorded snapshot
When a tree option is given on a real run that started and recorded a snapshot, the system SHALL show the tree after the run's summary, built from the changes recorded for that snapshot, exactly as `vara snapshot <id>` would show them, including a run that was cancelled or that had failed paths. A run that was refused before starting, so that no snapshot exists, SHALL show no tree. Reading the snapshot for the tree SHALL NOT modify profile backing storage. The tree SHALL NOT delay or alter the run itself, which completes and records its summary before the tree is read.

#### Scenario: Static tree after a real run
- **WHEN** a user runs `vara backup <profile> --depth 2` and the run records a snapshot
- **THEN** the run summary is shown, followed by the tree of that snapshot's changes limited to two levels, with the same counts `vara snapshot <id> --depth 2` would show

#### Scenario: Tree after a cancelled run
- **WHEN** a user runs `vara backup <profile> --files` and cancels the run with Ctrl+C after some changes were recorded
- **THEN** the run's cancellation summary is shown, followed by the tree of the changes recorded so far

#### Scenario: Refused run shows no tree
- **WHEN** a user runs `vara backup <profile> --interactive` and the run is refused because another run holds the target
- **THEN** the refusal is reported as usual and no tree is shown

#### Scenario: Run with no recorded changes
- **WHEN** a real run with a tree option records a snapshot that changed nothing
- **THEN** the system reports that no changes were recorded for the snapshot instead of an empty tree

### Requirement: Interactive backup tree and its fallback
`--interactive` on `vara backup` SHALL present the tree for keyboard navigation under the same keys, initial expansion, scrolling and terminal restoration as the interactive snapshot change tree, for both dry runs and real runs. The interactive view SHALL start only after the progress display has ended. When standard input or output is redirected, the terminal does not support live rendering, or color is disabled, the system SHALL show the static tree instead, honoring `--depth` and `--files`, and SHALL write a single line to standard error stating that the interactive view is unavailable.

Browsing SHALL NOT change the command's outcome: the exit code SHALL be the one the dry run or real run determined, whether the user quits the view, presses Ctrl+C in it, or the view falls back to the static tree.

#### Scenario: Interactive tree after a dry run
- **WHEN** a user runs `vara backup <profile> --dry-run --interactive` in a terminal and there are planned changes
- **THEN** the dry-run summary is shown, the interactive tree opens, and the user can expand, collapse and scroll it with the snapshot tree's keys

#### Scenario: Interactive tree after a real run
- **WHEN** a user runs `vara backup <profile> --interactive` in a terminal and the run records a snapshot
- **THEN** the run summary is shown, the interactive tree of the recorded changes opens, and quitting returns to the shell

#### Scenario: Fallback when output is redirected
- **WHEN** a user runs `vara backup <profile> --dry-run --interactive` with standard output redirected
- **THEN** the summary and the static tree are written to the output, one line on standard error states that the interactive view is unavailable, and the exit code matches the dry run's outcome

#### Scenario: Cancelled run shows the tree statically
- **WHEN** a user runs `vara backup <profile> --interactive`, cancels the run with Ctrl+C, and the run recorded a snapshot
- **THEN** the cancellation summary is shown, followed by the static tree of the recorded changes, the interactive view is not opened, and no fallback note is written

#### Scenario: Exit code unaffected by quitting
- **WHEN** a real run completes with the success outcome and the user then quits the interactive tree with `q`
- **THEN** the command exits with the success exit code

#### Scenario: Exit code unaffected by Ctrl+C in the tree
- **WHEN** a real run completes with the success outcome and the user then presses Ctrl+C in the interactive tree
- **THEN** the terminal is restored and the command exits with the success exit code

#### Scenario: Partial failure outcome preserved
- **WHEN** a real run ends with a partial-failure outcome and the user quits the interactive tree
- **THEN** the command exits with the partial-failure exit code
