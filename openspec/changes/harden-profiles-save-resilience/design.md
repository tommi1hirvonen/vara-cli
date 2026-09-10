## Context

See proposal.md - Why for motivation. Relevant existing shape:

- `Vara.Cli.Composition.ErrorReporting.Run(Action, IAnsiConsole?)` wraps every command action. Its
  `TryGetFriendlyMessage` switch lists the known domain exception types (including the abstract
  `ProfileConfigException`); anything else falls through to the catch-all that prints
  `Unhandled exception: <Type>: <message>` plus the stack trace. Either way it *returns an exit
  code* - the command is over.
- `ProfilesCommand.RunMenu` runs the whole interactive session inside one `ErrorReporting.Run`
  call, so any exception thrown by a Save or delete unwinds the main-menu loop and ends the
  session. `WriteProfiles` throws raw `IOException`/`UnauthorizedAccessException`, which is not in
  the friendly list.
- `Vara.Core.Configuration.ProfileConfigExceptions.cs` holds the load-side exception family
  (`ProfileConfigNotFoundException`, `ProfileConfigMalformedException`, `ProfileValidationException`,
  `DuplicateProfileNameException`, ...), all deriving from `ProfileConfigException`.
- `Program.cs` registers one `Console.CancelKeyPress` handler backed by
  `Composition.GracefulCancellation`: the first press calls `TokenSource.Cancel()` and returns
  `true`, which sets `ConsoleCancelEventArgs.Cancel = true` and *suppresses* termination; a second
  press calls `forceExit(ExitCodes.HardError)`. The token is threaded into `backup`, `restore`,
  `prune`, and `check` only. `ProfilesCommand.Create` takes no token.
- `YamlProfileConfigWriter` writes the document to a temp file via `StreamWriter`, disposes it,
  then `File.Replace`/`File.Move`s it into place, with a `finally` that deletes a surviving temp
  file.

## Goals / Non-Goals

**Goals:**
- Make a configuration-write failure a *recoverable event inside* the session rather than a
  session-ending exception, without weakening the existing behavior for genuinely unclassified
  exceptions.
- Give the user a working way out of any prompt, using the same Ctrl+C affordance every other
  command already honors.
- Close the durability gap between "we renamed a temp file into place" and "the temp file's bytes
  actually reached the disk".

**Non-Goals:**
- Changing when validation runs or what Save persists - that is `fix-profiles-draft-validation`'s
  scope.
- Adding an autosave, a crash-recovery draft file, or any form of resuming an interrupted session.
- Changing `ErrorReporting`'s behavior for other commands, or the two-press Ctrl+C contract that
  `backup`/`prune`/`check`/`restore` rely on.

## Decisions

### Catch the write failure at the call site, not in `ErrorReporting`
The natural-looking fix - adding `IOException` to `TryGetFriendlyMessage` - is wrong for this case:
`ErrorReporting.Run` terminates the command by design, so a "friendly" message there would still
end the session and lose the draft. Recovery has to happen where the session state lives. The Save
and delete actions therefore wrap their `WriteProfiles` call and, on failure, print the error in
the standard hard-error style and continue their loop (Save stays on the edit screen with the
draft intact; delete returns to the main menu with the profile still listed, and the in-memory list
is only mutated once the write has succeeded).

`ErrorReporting` remains the outer safety net for anything genuinely unexpected.

Alternative considered: catch broadly (`catch (Exception)`) around the whole menu loop and
re-enter it. Rejected - it would swallow programming errors and turn them into an unbreakable
loop; the recovery point must be the specific, expected I/O failure.

### Order of operations on delete: write first, mutate after
Today `ApplyDelete` removes the profile from the in-memory list and *then* writes. If the write
fails, the in-memory list no longer matches the file, and the main menu silently shows the profile
as deleted. The write is therefore performed against a candidate list, and the session's list is
replaced only after the write succeeds - so a failed delete leaves the session exactly as it was,
which is what the "no change" scenario requires. The same ordering applies to Save's add/replace.

### The writer raises a profile-configuration write exception
`YamlProfileConfigWriter` wraps its I/O in a new `ProfileConfigWriteFailedException` (a
`ProfileConfigException`) carrying the configuration path and the underlying exception's message.
This gives the editor one exception type to catch instead of enumerating I/O exception types at
the call site, gives the message a consistent shape ("Could not write ... : <reason>"), and - via
`ProfileConfigException` already being in `TryGetFriendlyMessage` - means any *other* caller of
the writer gets a clean, friendly error instead of a stack trace for free.

Alternative considered: have the editor catch `IOException`/`UnauthorizedAccessException`/
`SecurityException` directly. Rejected as an open-ended list that leaks the writer's implementation
choice of file APIs into the CLI layer.

### Ctrl+C: cancel the session, do not cooperate with it
Spectre.Console's prompts block on `Console.ReadKey`; a cancellation token cannot interrupt a
prompt that is already waiting. The two viable shapes are (a) let Ctrl+C terminate the process for
this command, or (b) rework the prompts to be cancellable.

This design takes (a): `ProfilesCommand` is given the shared `GracefulCancellation` token and, at
command entry, registers a callback on it that exits the process with the success exit code (the
interactive editor has no work to drain, and an interrupt is defined as a discard, not a failure).
Because the editor never writes outside an explicit Save/delete confirm, exiting at an arbitrary
prompt cannot leave a partial write - the atomic-write guarantee covers the only window where an
exit could land mid-write. This keeps the process-wide two-press contract intact for the
long-running commands while making a *single* press do the obvious thing in `profiles`.

Alternative considered: rebuild the menu/edit screens on cancellable input so Ctrl+C unwinds to
"Discard" and then to the main menu. Rejected as disproportionate - it means replacing
Spectre's prompt input handling for this command only, for a user-visible outcome (nothing was
written; the command ended) that is identical to exiting.

Alternative considered: also support Escape as "back/cancel" on each prompt. Rejected for this
change: it is a UX addition rather than a fix, every screen already has an explicit
Back/Discard/Quit row, and Spectre's selection prompts do not surface an Escape result without
custom input handling.

### Durability: flush the temp file's contents before the swap
The temp file is written through a `StreamWriter` whose `Dispose` flushes to the OS but does not
force the file system to commit the data. After the rename, a power loss can therefore leave a
renamed-but-empty file on some file systems. The writer explicitly flushes the underlying stream
to disk before closing it and performing the swap. The parent directory entry is not additionally
synced - .NET exposes no portable API for that, and the practical exposure (a rename that survives
without its data) is what the file flush addresses.

Alternative considered: write, rename, then flush. Rejected - it inverts the ordering the
guarantee depends on.

## Risks / Trade-offs

- [Continuing the session after a write failure could mask a persistent problem, letting the user
  retry Save repeatedly against a read-only file] → Each attempt reports the concrete reason, and
  the alternative (ending the session and discarding the draft) is strictly worse; the user can
  always Discard/Quit.
- [Exiting the process on Ctrl+C means the `profiles` command no longer honors the "second press
  forces exit" escalation] → Intentional: there is nothing to drain, so escalation has no meaning
  here; the token's behavior for every other command is untouched.
- [Forcing a flush per save adds a disk sync to an interactive action] → One sync per explicit
  Save/delete on a file of a few kilobytes; imperceptible next to the prompt round-trips.
- [A new exception type slightly widens the profile-config exception family] → It sits alongside
  the existing load-side family and inherits its reporting behavior, so no new handling is needed
  anywhere else.

## Migration Plan

No configuration or data migration. `profiles.yml`'s format is unchanged and the new exception type
is internal to the process. Rollback is reverting the commit; a file written by the new writer is
byte-identical to one written by the old writer.

## Open Questions

None.
