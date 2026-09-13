## Why

Two practical issues make `vara`'s interactive terminal UI feel rough: the `restore`
command's live progress bar renders unusably small (or wraps) whenever the file/directory
path used as its task label is long, because Spectre.Console measures that label's full,
untruncated text before laying out the row, and a long path steals width away from the bar
during that measurement even though the label itself would later render truncated anyway.
Separately, the `profiles` interactive screen never clears the terminal between renders,
so re-entering the profile edit screen (e.g. after choosing Discard and picking a profile
again) stacks a second copy of the profile's summary block underneath the first, and every
loop iteration within one edit session compounds the same problem.

## What Changes

- Add a shared path-label truncation helper that shortens a path to a fixed character
  budget by dropping leading parent directories and prefixing with `...` (e.g.
  `.../parent/dir/file.txt`), falling back to truncating the filename itself if even that
  does not fit.
- Apply that truncation to the task label built by `RestoreCommand.RunWithLiveDisplay`
  (single-file restore) and `RestoreCommand.RunDirectoryRestoreWithLiveDisplay` (recursive
  restore) before the label is handed to Spectre's progress display, so the label's raw
  text can no longer force the progress bar below a usable width.
- Clear the console at the top of each `RunEditScreen` loop iteration (immediately before
  `ProfileDraftPresenter.Render`) and once more before returning to the `profiles` main
  menu (via Save or Discard), so a previously rendered screen is never left behind
  underneath a newly rendered one.

## Capabilities

### New Capabilities

(none)

### Modified Capabilities

- `progress-reporting`: the restore command's live-display task label is now bounded to a
  fixed width via truncation, instead of rendering the full path and letting it distort the
  progress bar's width.
- `profile-management`: the profile edit screen now clears the terminal before each render
  and before returning to the main menu, instead of leaving prior renders on screen.

## Impact

- `src/Vara.Cli/Presentation/` gains a new shared truncation helper.
- `src/Vara.Cli/Commands/RestoreCommand.cs`: both live-display task-creation call sites
  build their label through the new helper.
- `src/Vara.Cli/Commands/ProfilesCommand.cs`: `RunEditScreen` clears the console before
  rendering the draft and before returning to the main menu.
- No changes to on-disk data, configuration format, or command-line arguments; purely
  terminal-rendering behavior.
