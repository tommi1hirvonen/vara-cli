## ADDED Requirements

### Requirement: Interactive file symbols remain readable without Unicode or color
The change symbols of the interactive tree's file rows (`+`, `~`, `->`, `-`, `@`) SHALL be ASCII characters, so that they need no replacement on a console that cannot display Unicode. Each row's change kind SHALL be identifiable from its symbol without relying on color. The toggle indicators on the status line SHALL be text that is legible without color.

#### Scenario: Console without Unicode support
- **WHEN** the interactive tree lists files on a console that cannot display Unicode
- **THEN** every file symbol is shown unchanged and each row's kind can be told from its symbol

#### Scenario: No color
- **WHEN** the interactive tree lists files where color is unavailable or the user cannot tell colors apart
- **THEN** each row's change kind is still identifiable from its symbol, and the status line still states whether deleted rows and file rows are shown
