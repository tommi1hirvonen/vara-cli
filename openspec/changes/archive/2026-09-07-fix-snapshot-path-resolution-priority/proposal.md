## Why
`SnapshotPathResolver.TryResolve` (used by `history`, `restore`, `show`, and `diff`) tries a
literal match against the raw input first, and only falls back to resolving it against the
current working directory if no literal match exists. When a user runs one of these commands from
inside a mirror subdirectory and types a path relative to where they're standing (for example,
`vara history file.txt` from inside `<mirror>\Projects\`), that literal string can coincidentally
match a different, unrelated path already recorded in history (for example a root-level
`file.txt`) - and that unrelated match wins, silently, over the file the user actually meant. The
spec's own resolution order currently mandates this literal-first behavior, so this is a
deliberate ordering that needs to change, not an implementation bug diverging from an existing
spec.

## What Changes
- WHEN the current working directory resolves inside the profile's live mirror, try the
  cwd-relative-to-mirror-relative interpretation (today's rule 2) *before* the literal
  interpretation (today's rule 1).
- WHEN the current working directory does not resolve inside the mirror, resolution order is
  unchanged: literal first, then cwd-relative-as-source-path (today's rule 3).
- The literal interpretation and the cwd-relative-as-source-path interpretation are never removed
  - both remain tried, just possibly after the cwd-relative-to-mirror interpretation when the
  working directory is inside the mirror.

## Capabilities

### Modified Capabilities
- `snapshot-history`: "Flexible path input for history, restore, show, and diff" reorders its
  resolution rules so a working directory inside the mirror takes priority over a coincidental
  literal match elsewhere in the mirror.

## Impact
- `Vara.Application.History.SnapshotPathResolver.TryResolve`.
- A user working inside a mirror subdirectory now gets the file at their own location when its
  name happens to collide with an unrelated recorded path elsewhere in the mirror.
- No change for a user running these commands from outside the mirror (the dominant, literal-path
  invocation pattern for scripts and muscle-memory usage) or for any path with no such collision.
