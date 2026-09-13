## 1. Alternate Screen Buffer lifecycle & cleanup

- [ ] 1.1 In `ProfilesCommand.Create`, wrap interactive menu execution inside `console.AlternateScreen(...)`, ensuring the primary terminal screen buffer is entered before `RunMenu` runs and restored upon completion.
- [ ] 1.2 Update cancellation registration and error handling in `ProfilesCommand` so that an interrupt (Ctrl+C) or unhandled error executes terminal buffer cleanup rather than terminating immediately with a dirty terminal state.

## 2. Screen and sub-screen clearing

- [ ] 2.1 In `ProfilesCommand.RunMenu`, clear the alternate screen (`console.Clear()`) before rendering the main menu prompt, and display any pending status message (such as a profile save or delete confirmation).
- [ ] 2.2 In `ProfilesCommand.RunEditScreen`, replace `ClearOwnRegion` and `screenStartRow` tracking with `console.Clear()` at the start of each redraw loop. Remove `ClearOwnRegion` entirely.
- [ ] 2.3 In `ProfilesCommand.RunSourcesScreen`, call `console.Clear()` at the start of each redraw loop before rendering the sources list and prompt.
- [ ] 2.4 In `ProfilesCommand.EditSource` and `ProfilesCommand.EditStringList`, call `console.Clear()` at the start of each redraw loop before rendering source/list state and prompts.

## 3. Post-session outcome reporting

- [ ] 3.1 Track whether a profile was saved during the session, and upon exiting the alternate screen buffer back to the primary terminal buffer, write a completion message to standard output if applicable.

## 4. Verification

- [ ] 4.1 Run the full test suite (`dotnet test`) and verify all automated tests pass, ensuring non-interactive/redirected checks in `ProfilesCommandTests` continue to function as expected.
- [ ] 4.2 Manually verify in an interactive terminal: launch `vara profiles`, enter a profile, manage sources, add multiple sources, toggle options, and confirm that each screen redraws cleanly with no duplicate menus or leftover lines. Verify that quitting `vara profiles` cleanly restores previous shell history and scrollback without corruption.
