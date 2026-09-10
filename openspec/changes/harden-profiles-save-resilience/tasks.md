## 1. A write failure becomes a recognizable, friendly error

- [ ] 1.1 Add a `ProfileConfigWriteFailedException` to
      `src/Vara.Core/Configuration/ProfileConfigExceptions.cs`, deriving from
      `ProfileConfigException`, carrying the configuration path and the underlying failure reason
      in its message, and verify it compiles alongside the existing load-side exception family
- [ ] 1.2 Wrap `YamlProfileConfigWriter.WriteProfiles`'s file I/O so that an `IOException`,
      `UnauthorizedAccessException`, or comparable I/O failure is rethrown as
      `ProfileConfigWriteFailedException` (preserving the original as `InnerException`), and
      verify a unit test in `tests/Vara.Infrastructure.Tests/Configuration/YamlProfileConfigWriterTests.cs`
      that makes the destination unwritable asserts the new exception type and a message naming
      the configuration path
- [ ] 1.3 Verify `ErrorReporting.TryGetFriendlyMessage` already classifies the new exception (it
      derives from `ProfileConfigException`) by a test asserting a thrown
      `ProfileConfigWriteFailedException` is reported in the hard-error style with no stack trace

## 2. The `profiles` session survives a failed write

- [ ] 2.1 Change `ProfilesCommand.ApplySave` so the configuration file is written *before* the
      session's in-memory profile list is updated (write a candidate list, then commit the change
      to the live list only on success), and verify a test that injects a throwing
      `IProfileConfigWriter` asserts the in-memory list is unchanged after the failure
- [ ] 2.2 Apply the same write-then-commit ordering to `ProfilesCommand.ApplyDelete`, and verify a
      test with a throwing writer asserts the profile is still present in the in-memory list
- [ ] 2.3 Catch `ProfileConfigWriteFailedException` in the edit screen's Save action: report it
      via `OutcomeStyle.WriteLineError`, stay on the edit screen with the draft intact, and do not
      return to the main menu; verify a test asserts the draft's fields are unchanged after a
      failed save and that a subsequent save with a working writer succeeds without re-entering
      any field
- [ ] 2.4 Catch `ProfileConfigWriteFailedException` in the delete flow: report it and return to
      the main menu with the profile still listed; verify a test asserts the profile remains in
      the list after a failed delete
- [ ] 2.5 Verify by inspection that no `WriteProfiles` call in `ProfilesCommand` can still unwind
      past `RunMenu` into `ErrorReporting.Run` (grep for `WriteProfiles` in `src/Vara.Cli`)

## 3. Ctrl+C exits the profiles editor

- [ ] 3.1 Thread the shared `GracefulCancellation.TokenSource.Token` into
      `ProfilesCommand.Create(...)` from `Program.cs`, matching how `backup`/`prune`/`check`/
      `restore` already receive it, and verify `vara profiles --help` is unchanged and the
      solution builds
- [ ] 3.2 Register a callback on that token at command entry (after the interactive-terminal
      check) that exits the process with the success exit code, so a single Ctrl+C at any prompt
      ends the command promptly; verify the callback registration is disposed when the command
      returns normally so it cannot fire for a later command in the same process
- [ ] 3.3 Verify by test that the cancellation callback performs no write: assert that invoking
      the registered exit path with a recording `IProfileConfigWriter` leaves `WasCalled` false,
      and confirm manually that Ctrl+C in an interactive `vara profiles` session exits with the
      configuration file byte-identical to before the draft was opened

## 4. Durable write

- [ ] 4.1 Flush the temporary file's contents to durable storage before it is closed and swapped
      into place in `YamlProfileConfigWriter` (force the underlying stream to disk rather than
      relying on `StreamWriter.Dispose`'s buffer flush), and verify the existing writer tests -
      round-trip, first-write, overwrite, failure-during-swap, and no-temp-file-left-behind - all
      still pass
- [ ] 4.2 Verify by inspection that the flush happens before the `File.Replace`/`File.Move` swap,
      not after, so the ordering the atomicity guarantee depends on is preserved

## 5. Cross-cutting verification

- [ ] 5.1 Run `dotnet test` and confirm the whole suite passes, with no regression in the existing
      profile-config, writer, backup, restore, prune, or Ctrl+C/`GracefulCancellation` tests
- [ ] 5.2 Build with `dotnet build Vara.slnx -c Debug --nologo -v quiet` and confirm it succeeds
      with `PublishAot=true` still set on `Vara.Cli`
- [ ] 5.3 Manually verify the two-press Ctrl+C behavior of `backup`/`prune`/`check` is unchanged
      by this change's token wiring
