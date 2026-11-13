## Context

`TreeBrowser` (see the archived `interactive-change-tree` change) keeps a `TreeState` of `Directories: RollupDirectory list`, an expanded set, a cursor row identity and a scroll offset. On every `step` and every `frame` it derives the visible rows from scratch: `childrenOf` filters the whole directory list once per directory, `settle` and `step` scan the row list with `List.exists`, `List.tryFindIndex` and `List.item`. That is cheap for directories and was the stated assumption, but it is quadratic in directories and linear per key press, and it will not hold once files are rows.

Files already exist in the data: `RollupDirectory.Files` is filled by `History.rollup` when `includeFiles` is true, and the interactive session is given them only when `--files` was passed. In a snapshot, a move is recorded as a `Moved` row at the new path (with `MovedFrom`) and a `Deleted` row at the old path. In a dry run, `Run.entriesOfPlan` emits one `Moved` entry and no deleted entry for the old path. A rollup of 200,000 file entries allocated about 11 MB without files and 52 MB with per-directory file lists. See proposal.md for scope.

## Goals / Non-Goals

**Goals:**
- File rows, symbols and the two toggles in the interactive tree, for `snapshot` and `backup`.
- Cursor movement and repaint that do not depend on the number of directories or files.
- No change to the static tree, to counts, or to the plan.

**Non-Goals:**
- Per-kind toggles other than `d`, search, filtering, or acting on a row.
- Making the dry run emit deleted entries for moves.
- Streaming the tree before the data is loaded.

## Decisions

**1. The browser indexes its input once.** `TreeBrowser.create` takes the directory rollup (counts, as today) and a flat array of `ChangeEntry`. It builds a `TreeIndex` once: directory nodes in an array, each with its parent index, an ordered array of child node indexes and a `FileStart`/`FileCount` range; and one array of file entries ordered by directory and then file name (ordinal, ignoring case), so a directory's files are a slice, not a list. Directory lookup is by case-insensitive path in a dictionary built once. Alternative considered: keep `RollupDirectory.Files` lists. Rejected because it costs 52 MB against 11 MB at 200,000 entries and gives no cheap way to slice, count, or filter by kind.

**2. Visible rows are cached in the state.** The state holds the visible rows as an array and recomputes it only when the expansion set, `ShowDeleted` or `ShowFiles` changes. The cursor is stored as an index into that array and as the row's identity; a recompute re-finds the identity, falling back to the nearest visible ancestor, which is the rule the specs state for hidden rows. `Up`, `Down`, `Home`, `End` and every repaint then cost O(1) plus the window's height. The frame slices the array to the window and never builds text for rows outside it. Alternative considered: keep deriving rows per key press and optimize the scans. Rejected because the cost is inherent in deriving; caching is simpler and removes it.

**3. The row model.** A visible row is a directory node index or a file entry index. Directory rows carry the existing label and counts. File rows carry the symbol, tone and name. Indentation and tree guides follow the existing rules, with file rows after child directories. The detail line is produced from the cursor row only: the directory's full path, or the file's full path and, for a moved entry, its `MovedFrom`.

**4. Symbols.** `+`, `~`, `->`, `-` and `@`, padded to two columns, with the change-kind tone from the existing mapping. They are ASCII, so `Display.toAscii` needs no change, and they are the glyphs the counts already use (`link` becomes `@` on a row because a one-character marker fits a row).

**5. Move absorption is decided once, at index build.** The index collects the `MovedFrom` paths of all `Moved` entries into a case-insensitive set. A `Deleted` entry whose path is in that set is flagged absorbed and is left out of the file array's visible set (it stays in the array's counts source, the rollup, so directory counts are unchanged). It is not counted in either toggle's hidden number. In a dry run there are no such entries, so the rule has no effect there. Alternative considered: make the plan emit a deleted entry per move for parity. Rejected for now because it changes the "tree totals match the dry-run counts" requirement of `plan-change-tree` and the summary's meaning; the pairing gives the same one-row view in both modes.

**6. Toggles are state, not data.** `ShowDeleted` and `ShowFiles` default to true. Toggling recomputes the cached rows (decision 2) and nothing else. Hidden counts are computed once at index build (non-absorbed deleted entries; all file entries) and shown from the state: `d` hides the deleted count, `f` hides the file count, and when both apply the status line shows both. An expandable directory is one with a child node or a visible file; so with `f` off a leaf directory loses its marker and Right does nothing on it, and `*` expands only directories that have child directories.

**7. Keys.** `Key` gains `ToggleDeleted` and `ToggleFiles`. `KeyInput` maps `d`/`D` and `f`/`F`. `step` handles them; unassigned keys remain ignored.

**8. Loading files for the interactive view.** `SnapshotCommand` reads the snapshot's rows with files when `--interactive` is given or `--files` is given, and passes both the rollup and the entries to the browser. `BackupCommand` does the same for a real run's recorded snapshot (`ChangeTree.readSnapshot` takes the include-files flag from `Files || Interactive`) and for a dry run, whose entries it already holds. `ChangeTree.present` passes the entries through to `TreeBrowser.create`. The static tree continues to use `RollupDirectory.Files` and `--files` only.

**9. Frame layout.** Header, then the window, then the detail line, then the status line. The window gets the terminal height less the header and those two lines, and at least one row, as before. The status line carries the toggle states, the hidden counts, the more-above and more-below counts and the key hints; it is plain text so it needs no color. The hints are shortened to fit narrow terminals.

## Risks / Trade-offs

- [Building the index for hundreds of thousands of entries before the first frame delays opening] → One sort and one pass; measure with the 200,000-entry benchmark before and after, and require the first frame in well under a second on that size.
- [Expand All on a large snapshot makes a very long row array] → Rows are small structs and only the window is rendered; measure peak allocation in the benchmark.
- [A directory's deleted count can exceed its visible deleted rows because of absorbed moves] → Counts match the static report by design; the detail line for a directory says how many deleted entries are absorbed by moves under it, so the difference is explained.
- [`f` hides everything under a leaf directory, which then looks empty] → The marker disappears and the status line says file rows are hidden, so the state is visible.
- [Changing `create`'s inputs touches every browser test] → Keep the existing tests' scenarios and port their fixtures through one helper that builds both the rollup and the entry array from the same changes.
- [The interactive view now always loads file rows, using more memory than before for large snapshots] → The index's sorted array is about 32 bytes per entry, below the per-directory lists it replaces; confirmed by the benchmark.

## Open Questions

- Whether the `?` key should show a help overlay is deferred; the status line hints cover the keys for now.

## Measurements

`TreeBrowserBenchmarks`, `--job short`, about 2,000 directories, files listed (`includeFiles` true), run from `vara-cli-scratch`. Allocations are the asserted figures.

Before (the browser as archived, commit `2cf4f6d`):

| Operation | Entries | Mean | Allocated |
|---|---:|---:|---:|
| Create | 20,000 | 1.5 us | 7.5 KB |
| Expand All + one frame | 20,000 | 10.8 ms | 8.2 MB |
| 20 cursor steps, each with a frame | 20,000 | 497 ms | 327 MB |
| Create | 200,000 | 1.4 us | 7.5 KB |
| Expand All + one frame | 200,000 | 116 ms | 82 MB |
| 20 cursor steps, each with a frame | 200,000 | 4,763 ms | 3,265 MB |

About 240 ms and 160 MB per key press at 200,000 entries, so the current browser is not usable with file rows on a large snapshot.

After (the indexed browser with cached rows, same benchmark and machine state):

| Operation | Entries | Mean | Allocated |
|---|---:|---:|---:|
| Create (index and first rows) | 20,000 | 6.7 ms | 5.4 MB |
| Expand All + one frame | 20,000 | 6.9 ms | 7.1 MB |
| 20 cursor steps, each with a frame | 20,000 | 59 us | 492 KB |
| Create (index and first rows) | 200,000 | 94.6 ms | 48.6 MB |
| Expand All + one frame | 200,000 | 99.6 ms | 62.8 MB |
| 20 cursor steps, each with a frame | 200,000 | 60 us | 500 KB |

Twenty cursor steps with frames went from 4,763 ms and 3,265 MB to 60 us and 500 KB at 200,000 entries, and no longer depend on the entry count (59 us at 20,000). Expand All with a frame improved (116 ms to 100 ms, 82 MB to 63 MB). The one cost that grew is opening: the old `Create` did no work (its rows were derived at the first frame), the new one builds the index and the first rows once, 6.7 ms at 20,000 entries and 95 ms at 200,000. Results are stored in `benchmarks/baselines/2026-11-13-tree-browser-fsharp-net11` (`before` and `after`).
