## Why

`vara profiles` is a long-lived interactive session that holds unsaved work, but it has no
recovery path for the two things most likely to go wrong mid-session. A failure while writing
`profiles.yml` (read-only file, permission denied, disk full, the file locked by another process)
is not recognized as a known error condition, so it escapes the menu loop entirely: the user sees
`Unhandled exception: IOException` plus a raw stack trace, the command exits, and every edit in the
draft is lost - directly contradicting the intent that the draft survives a failed write so Save
can be retried. Separately, the process installs a global Ctrl+C handler that suppresses
termination and hands a cancellation signal to long-running commands; `vara profiles` observes no
such signal and its prompts offer no cancel key, so the first Ctrl+C appears to do nothing at all
and the only way out is to navigate back to "Quit" (or press Ctrl+C a second time to force-kill).

## What Changes

- A failure to write the configuration file (on Save or on delete-confirm) is reported as a clear,
  styled error naming the configuration file and the underlying reason, and the session stays open
  on the screen the user was on with their draft intact, so the write can be retried or the draft
  discarded deliberately - instead of terminating the command with an unhandled stack trace.
- Pressing Ctrl+C anywhere in `vara profiles` exits the command promptly without writing to the
  configuration file, so a user is never trapped in a prompt; the exit is treated as a discard of
  any in-progress draft, consistent with "Discard".
- The configuration file write flushes the temporary file's contents to durable storage before the
  atomic move, so the guarantee that the file is never left truncated or partially written also
  holds across an abrupt loss of the process or machine, not only an orderly interruption.

## Capabilities

### New Capabilities
(none)

### Modified Capabilities
- `profile-management`: adds failed-write behavior to "Save persists only a fully valid draft" and
  "Deleting a profile requires explicit confirmation"; adds an interrupt/exit path to "Main menu
  lists profiles and top-level actions"; strengthens the durability wording of "Saving regenerates
  the whole configuration file without preserving prior formatting".

## Impact

- `Vara.Cli.Commands.ProfilesCommand`: the Save and delete actions gain a failure path that stays
  in the session; command entry participates in the process-wide cancellation signal.
- `Vara.Cli.Composition.ErrorReporting`: a configuration-write failure becomes a recognized,
  friendly-message error condition rather than falling through to the unhandled-exception path.
- `Vara.Infrastructure.Configuration.YamlProfileConfigWriter`: flush-before-move, and surfacing an
  I/O failure as a profile-configuration error rather than a raw `IOException`.
- `Vara.Core` may gain one profile-configuration exception type for write failures, alongside the
  existing load-side ones. No change to any domain type's validation rules.
- Other commands are unaffected: they already observe the shared cancellation token and already
  route their errors through the same reporting path.
