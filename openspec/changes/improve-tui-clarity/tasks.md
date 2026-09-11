## 1. Path label truncation helper

- [x] 1.1 Add a `PathLabelTruncator` (or similarly named) helper in `src/Vara.Cli/Presentation/`
  that, given a path and a fixed character budget, returns the path unchanged if it already fits,
  otherwise drops leading parent segments and prefixes the kept trailing segments with `...`, and
  falls back to truncating the filename itself when the filename alone exceeds the budget. Verify
  with unit tests covering: a short path (unchanged), a deeply nested path (parent-truncated,
  filename preserved), and a filename alone longer than the budget (filename-truncated).
- [x] 1.2 Add a small helper for deriving the label's character budget from a terminal width (bar
  width 40 + headroom for percentage/ETA/throughput columns + inter-column padding), clamped to a
  sane minimum floor. Verify with unit tests covering a wide terminal, a narrow terminal, and the
  clamped-floor case.

## 2. Restore command: single-file live display

- [x] 2.1 In `RestoreCommand.RunWithLiveDisplay`, compute the label budget from `console`'s
  reported width and build the task description from the truncated path instead of the raw
  `path` value. Verify by adding/adjusting a test in `RestoreCommandTests.cs` that asserts the
  task description passed to the progress display is bounded to the computed budget for a long
  path, while a short path's task description is unchanged.

## 3. Restore command: recursive directory live display

- [x] 3.1 Apply the same truncation to `RestoreCommand.RunDirectoryRestoreWithLiveDisplay`'s task
  description (built from `directoryPath`). Verify by adding/adjusting a test in
  `RestoreCommandTests.cs` covering a long directory path.

## 4. Profiles command: clear screen on redraw

- [x] 4.1 In `ProfilesCommand.RunEditScreen`, erase only the edit screen's own drawn region -
  never the whole terminal - before each render of `ProfileDraftPresenter`, and once more before
  returning to the main menu on both the `Save` and `Discard` branches. (Superseded during manual
  verification: an initial `console.Clear()` implementation cleared the entire terminal, including
  content unrelated to vara; replaced with `ClearOwnRegion`, which captures the screen's starting
  cursor row once and repositions/blanks only from that row downward - see design.md's "Scoped
  erase via cursor repositioning, not `IAnsiConsole.Clear()`" decision.)

  **Note:** No automated test was added for this task. `RunEditScreen`'s menu/navigation is
  driven entirely by `IAnsiConsole.Prompt` (`SelectionPrompt`), which reads raw terminal input;
  `ProfilesCommandTests.cs`'s own class doc comment already documents that this flow cannot be
  exercised from this automated, non-interactive test host (`Console.IsInputRedirected` is always
  true here, tripping `ProfilesCommand`'s interactive-terminal guard before any menu code runs),
  and no existing test in this codebase scripts input through a `SelectionPrompt`. This matches
  that established, pre-existing convention rather than introducing new test infrastructure
  beyond it. Verification for this specific fix is covered by task 5.2's manual check instead.

## 5. Verification

- [x] 5.1 Run the full `Vara.Cli.Tests` suite (`dotnet test tests/Vara.Cli.Tests`) and confirm all
  tests pass, including the new/updated tests from tasks 1-4.
- [ ] 5.2 Manually verify in a real terminal: `restore` (single-file and `--recursive`) on a
  deeply nested path shows a full-width progress bar with a shortened path label; `profiles` ->
  select a profile -> Discard -> select a profile again shows the profile summary exactly once,
  with no stale content above it.
