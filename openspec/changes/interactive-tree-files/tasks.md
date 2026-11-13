## 1. Benchmark the browser as it is

- [ ] 1.1 Add a `TreeBrowserBenchmarks` scenario to `benchmarks/Vara.Benchmarks` that builds a synthetic rollup of 20,000 and 200,000 entries over a few thousand directories and measures creating the state, pressing Expand All, and a stream of 1,000 cursor steps each followed by a frame; verify it runs with `--job short`
- [ ] 1.2 Run it against the current browser from `vara-cli-scratch` and keep the numbers in the change's design notes as the "before" figures; verify they include allocations and a time for each of the three operations

## 2. Index and cached rows (no behavior change)

- [ ] 2.1 Build the `TreeIndex` (directory nodes with parent and child arrays, sorted entry array with per-directory ranges, case-insensitive lookup) from the rollup and a `ChangeEntry` array, and verify Domain tests for ordering, ranges and lookup on a small tree
- [ ] 2.2 Cache the visible rows in `TreeState` and recompute them only when the expansion set changes, with the cursor kept as an index plus an identity that falls back to the nearest visible ancestor; verify every existing `TreeBrowserTests` scenario passes with its fixtures ported through one helper
- [ ] 2.3 Make `frame` slice the cached rows to the window and `step` O(1) for cursor moves; verify with the benchmark that cursor steps and frames no longer grow with the directory count, and record the "after" figures next to the "before" ones

## 3. File rows and symbols

- [ ] 3.1 Add file rows from the index (after child directories, ordered by name, ordinal ignoring case), with the symbol and tone per change kind and the file name, and verify Domain tests for the five kinds, the ordering and the deepest directory
- [ ] 3.2 Add the detail line (full path of the cursor row, and the previous path of a moved file) and adjust the window size for it; verify tests for a directory row, a file row and a moved file, and that the cursor row is always inside the window with a tiny height
- [ ] 3.3 Absorb a deleted row whose path is a moved entry's previous path, keeping the directory counts as they are, and add the absorbed count to a directory's detail line; verify tests for a move, for counts equal to the static report, for a plain deletion, and for a case-insensitive match
- [ ] 3.4 Verify the ASCII requirements with a test that the frame, passed through `Display.mapTexts Display.toAscii`, contains only ASCII, and that every symbol identifies its kind without color

## 4. Toggles

- [ ] 4.1 Add `ToggleDeleted` and `ToggleFiles` to `Key`, map `d`/`D` and `f`/`F` in `KeyInput`, and add `ShowDeleted` and `ShowFiles` (both true) to the state; verify tests that each key flips its flag and leaves directory counts and the expansion set untouched
- [ ] 4.2 Recompute the rows on a toggle with the expandability rule (a child directory or a visible file) and the cursor fallback, and verify tests for a leaf directory losing its marker, Right doing nothing on it, `*` expanding only directories with children, and the cursor on a hidden deleted row moving to its directory
- [ ] 4.3 Show the toggle states and the hidden counts on the status line, with the absorbed rows not counted, and shorten the hints for narrow terminals; verify tests for deleted shown, deleted hidden with a count, files hidden with a count, and both hidden

## 5. Command wiring

- [ ] 5.1 Make `vara snapshot --interactive` read the snapshot's rows with files and pass the entries to the browser, with `--files` accepted and having no further effect; verify CLI tests that files appear without `--files`, that `--files` changes nothing, and that the static tree output is unchanged
- [ ] 5.2 Make `vara backup --interactive` use the dry run's entries and, for a real run, read the recorded snapshot with files; verify CLI tests for both modes with a scripted session that expands a leaf directory and sees its files
- [ ] 5.3 Add a CLI test with a snapshot that recorded a move and verify the interactive tree shows one moved row and no deleted row for the old path while the directory counts match `vara snapshot` output

## 6. Documentation and checks

- [ ] 6.1 Update the README and the `snapshot` and `backup` help text for the file rows, the symbols and the `d` and `f` keys; verify `--help` for both commands shows them
- [ ] 6.2 Run the full test suite, then the browser benchmark on 200,000 entries from `vara-cli-scratch`; verify the first frame, Expand All and cursor-step costs are no worse than the "before" figures at 20,000 entries and that the 200,000-entry case completes, and store the BenchmarkDotNet results in a new baseline folder with a row in `benchmarks/baselines/README.md`
- [ ] 6.3 Run the release check from `vara-cli-scratch` against the latest release-check baseline because `snapshot` and `backup` changed, and verify the verdict; record it in a new baseline folder only if the user asks
- [ ] 6.4 Manually run `vara snapshot <id> --interactive` and `vara backup <profile> --dry-run --interactive` on Windows Terminal, covering file rows, the symbols, `d` and `f`, a moved file, scrolling and a narrow terminal, and record the result
