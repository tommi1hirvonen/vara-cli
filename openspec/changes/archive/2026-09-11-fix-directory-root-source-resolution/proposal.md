## Why

`browse` and `restore --recursive` accept a directory argument that is supposed to follow the
same flexible-path resolution as `history`/`restore`/`show`/`diff` (mirror-relative, absolute
source path, or a path relative to the current working directory). That works correctly when
the current working directory is inside the profile's mirror, and it works correctly for any
non-root directory name. But when the argument is `.` (or empty) - "browse/restore my current
directory" - and the current working directory is inside the profile's *source* tree instead of
the mirror, both commands always resolve it to the mirror root instead of the source-mapped
subdirectory the user is actually standing in. This is because each command's "is this directory
tracked" check treats the mirror root as trivially always tracked, so it short-circuits before
the absolute-source-path mapping step ever runs - unlike a real (non-root) directory or file
name, which only matches literally when something is actually recorded there. Single-file
`restore`/`history`/`show`/`diff` already work correctly with relative paths from the source
tree; directory browsing and recursive restore should behave the same way.

## What Changes

- For `browse` and `restore --recursive`, when the current working directory does not resolve
  to a location inside the profile's mirror, resolving a directory argument of `.` (or blank)
  SHALL first try mapping the current working directory through the same absolute-source-path
  translation used for any other tracked path, before falling back to the mirror root.
- If that source-mapped location has no tracked history at all, the command SHALL fall back to
  the mirror root as before, and SHALL print a brief, clear message explaining that the current
  directory isn't tracked and the mirror root is being shown instead, so the user isn't
  surprised by an unexpected listing.
- No change to resolving a non-root directory argument (e.g. `browse subdir`), which already
  falls through to the absolute-source-path mapping correctly.
- No change to `history`/`restore`/`show`/`diff`'s existing single-file flexible-path
  resolution order.

## Capabilities

### New Capabilities

(none)

### Modified Capabilities

- `snapshot-history`: the "Flexible path input" requirement is extended to explicitly cover the
  directory argument accepted by `browse` and `restore --recursive`, including the `.`/current-
  working-directory root case and the fallback-with-message behavior when the source-mapped
  location has no tracked history.

## Impact

- `src/Vara.Cli/Commands/DirectoryArgumentResolver.cs`: root-tracked check must no longer
  short-circuit before the source-path mapping is attempted; needs to report whether it
  ultimately fell back to the root so the caller can message the user.
- `src/Vara.Cli/Commands/BrowseCommand.cs`: print the fallback message when applicable.
- `src/Vara.Cli/Commands/RestoreCommand.cs` (`IsDirectoryTracked` / `RunRecursiveRestore`): same
  root-short-circuit issue for `restore --recursive`; needs the same fix and messaging.
- Existing tests `tests/Vara.Cli.Tests/Commands/DirectoryArgumentResolverTests.cs` (notably
  `The_mirror_root_itself_always_resolves`) encode the old behavior and need updating alongside
  new coverage for the source-mapped-root and fallback-with-message cases.
