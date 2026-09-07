## Context
`SnapshotHistoryService.ShowVersion` opens the resolved version's content via
`contentStore.OpenRead` and calls `source.CopyTo(destination)` with no inspection. `LooksBinary`
(private to `SnapshotHistoryService`, used by `OpenVersionsForDiff`) already implements exactly the
sampling/rewind logic needed: it reads up to `BinarySampleSize` (8000) leading bytes, checks for a
NUL byte, and resets `stream.Position = 0` so a subsequent full read still sees the content from
the start - it works on any seekable stream, and `contentStore.OpenRead` returns a seekable
`FileStream` today. `ShowCommand` currently writes straight to `Console.OpenStandardOutput()` with
no notion of whether that's a real terminal. `Console.IsInputRedirected` is already used elsewhere
in the CLI (`PruneCommand`) for an analogous interactive-vs-non-interactive decision;
`Console.IsOutputRedirected` is its output-side counterpart.

## Goals / Non-Goals

**Goals:**
- Reuse the existing binary-detection heuristic (`LooksBinary`) rather than adding a second
  implementation.
- Guard only when it protects something real: an interactive terminal. Redirected output (a file,
  a pipe) is never blocked.
- Give the user an explicit, discoverable way to force binary output to a terminal if they really
  want it.

**Non-Goals:**
- Changing `diff`'s existing binary guard or its unconditional refusal - `diff` has no legitimate
  "redirect it somewhere safe" use case the way `show` does, so its behavior is intentionally
  different and is left untouched.
- Detecting or handling terminal corruption after the fact (for example, resetting terminal state
  post-hoc) - the guard exists to avoid writing the bytes at all, not to clean up afterward.
- Any change to `RestoreVersion`/`RestoreAsOf` - they write to a file, never to a terminal, so this
  class of risk does not apply to them.

## Decisions
- **Move `LooksBinary` to a shared, reusable form on `SnapshotHistoryService`** (already private to
  the class; just called from a second method now) rather than duplicating the sampling logic -
  no new type needed, since both call sites already live on the same class.
- **`ShowVersion` takes the terminal/redirection decision as a parameter (`bool forceBinary`),
  not a `Console` dependency.** `SnapshotHistoryService` has no existing dependency on `Console`
  or any terminal concept; introducing one for a single check would be a one-off inconsistency.
  Instead, `ShowVersion` gains a boolean parameter meaning "skip the binary guard regardless of
  what the content looks like." `ShowCommand` computes that boolean as
  `Console.IsOutputRedirected || force-binary-option-given` and passes it in - keeping the
  terminal-detection concern in the CLI layer, where `OutputMode`/`Console.IsInputRedirected`
  precedent already lives, and the content-inspection concern in the application layer, where
  `LooksBinary` already lives.
- **New `ShowBinaryContentException`, not a reused `DiffBinaryContentException`.** The diff
  exception's message and shape are built around two named sides (`LeftIsBinary`/`RightIsBinary`);
  `show` has exactly one resolved stream. A small, single-sided sibling exception is clearer than
  force-fitting the two-sided type or adding unused fields to it.
- **Sample before opening the destination for writing, same as today's ordering** (resolve version,
  open content stream, check, then copy) - `ShowCommand` already opens `Console.OpenStandardOutput()`
  once outside `ShowVersion` and passes it in as `destination`; nothing changes about when that
  handle is opened, only whether `CopyTo` is reached.
- **Flag name: `--force-binary`, not `--force`.** `RestoreCommand`'s existing `--force` means
  "overwrite the destination without asking" - a different concern. Reusing that name for "stream
  binary content anyway" would be misleading on a command (`show`) that has no destination to
  overwrite in the first place.

## Risks / Trade-offs
- [Risk] `Console.IsOutputRedirected` can be inaccurate in unusual hosting scenarios (for example,
  some CI runners or terminal emulators that report a TTY inconsistently).
  -> Mitigation: same detection mechanism already trusted elsewhere in this codebase
  (`Console.IsInputRedirected` in `PruneCommand`); `--force-binary` remains available as a manual
  override regardless.
- [Risk] A user relying on `show`'s current unconditional streaming for a binary file to an
  interactive terminal (unlikely, but possible in an existing script) now sees an error instead.
  -> Mitigation: this is the intended behavior change; `--force-binary` restores the old behavior
  explicitly.
