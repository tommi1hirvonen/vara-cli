## 1. Spike: terminal loop

- [x] 1.1 Prototype key reading, alternate screen, hidden cursor, buffered frame repaint, resize polling and Ctrl+C-as-key in a throwaway console project or test harness; verify it on Windows Terminal and legacy conhost, and record the findings in design.md if any decision changes
- [x] 1.2 Confirm `Display.toAscii` covers the guide glyphs the browser reuses and verify with a test that the chosen markers (`[+]`, `[-]`, `>`) pass through `mapTexts` unchanged

## 2. Neutral change input

- [x] 2.1 Add `ChangeEntry` and a `FileVersion` to `ChangeEntry` mapping in the Domain history code, and add `rollupEntries`; verify with unit tests that mapped entries produce the same `RollupDirectory list` as before
- [x] 2.2 Re-express `History.rollup` over `rollupEntries` and verify the existing rollup and `HistoryView.ofRollup` tests pass unchanged
- [x] 2.3 Extract the shared `[source]` mark and counts-span helpers from `HistoryView.ofRollup` and verify static output tests still pass unchanged

## 3. Tree browser state machine

- [x] 3.1 Implement `TreeState`, the `Key` union and the visible-row derivation with initial expansion from the effective depth; verify tests for depth 0, the default, and depth 2 match the specs' scenarios
- [x] 3.2 Implement `step` for Up, Down, Home, End, Right, Left and ExpandAll with cursor-by-identity; verify tests for expand, move-into-first-child, collapse, move-to-parent, Left on the root, collapse with the cursor inside the collapsed subtree, and ignored keys
- [x] 3.3 Verify with a test that a directory's displayed counts are identical collapsed and expanded, and that `--files` rows are leaves with kind and moved-from text
- [x] 3.4 Implement the scrolling window (cursor visibility, hidden-row counts, no blank space after collapsing near the end, tiny heights) and verify with tests for each scenario in the scrolling requirement
- [x] 3.5 Implement `render` to `Line list` with the cursor gutter and style, state markers, counts in change-kind tones, and the status line; verify with rendering tests including a no-Unicode pass through `mapTexts`

## 4. Command integration

- [x] 4.1 Add the `KeyInput` record and the console implementation (alternate screen, cursor hide and restore in `finally`, Ctrl+C mapped to cancellation, resize detection); verify with tests using scripted keys that quit, Esc, Ctrl+C and an exception all run the restore action
- [x] 4.2 Add `--interactive` to `vara snapshot`, build the session from the manifest rows after the unknown-snapshot and empty-scope checks, and verify with CLI tests that those two cases never start the loop
- [x] 4.3 Implement the single fallback decision (redirected input or output, no color, `NO_COLOR`) printing the static tree and one line on standard error; verify CLI tests for each trigger, including that depth and `--files` are honored and the exit outcome matches the static report
- [x] 4.4 Verify `--interactive` combined with `--depth` and `--files`, and that an invalid `--depth` is still rejected before any session starts

## 5. Documentation and checks

- [x] 5.1 Update the README and the command help text for `--interactive` with the key bindings, and verify `vara snapshot --help` shows them
- [x] 5.2 Run the full test suite and the allocation benchmark for rollup and rendering on a large synthetic snapshot; verify no regression in the static path
- [x] 5.3 Manually run `vara snapshot <id> --interactive` against a real profile on Windows Terminal and conhost, covering navigation, scrolling, resize, Ctrl+C and redirected output, and record the result
