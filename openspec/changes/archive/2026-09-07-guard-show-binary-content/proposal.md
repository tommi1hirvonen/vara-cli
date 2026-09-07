## Why
`vara show` streams a resolved version's content directly to standard output with no check on
its nature. `vara diff` already refuses to compare binary content, detected from a bounded initial
sample of bytes, specifically so it never dumps binary data somewhere it doesn't belong. `show` has
no equivalent guard: requesting a binary version (an image, an executable, a database file) while
standard output is an interactive terminal streams raw, uncontrolled bytes directly at it, which
can garble the terminal (escape sequences, control characters) or make it appear to hang. Unlike
`diff`, `show` has a legitimate reason to stream binary content when output is redirected to a
file or another program (for example, `vara show photo.jpg --version 3 > photo.jpg`), so the fix
must not block that common, valid use.

## What Changes
- Before streaming a resolved version's content to standard output, `vara show` detects whether
  the content is binary using the same bounded-sample, NUL-byte heuristic `diff` already uses.
- WHEN standard output is an interactive terminal (not redirected to a file or pipe) and the
  content is detected as binary, the system refuses to stream it and reports a clear error
  instead, naming the path, the same way `diff` already reports a binary refusal.
- WHEN standard output is redirected (to a file or another program), or the user passes a new
  `--force-binary` option, the content is streamed exactly as today, since there is no terminal to
  protect and/or the user has explicitly opted in.

## Capabilities

### Modified Capabilities
- `snapshot-history`: "Show a version's content directly" gains a binary-content guard when
  standard output is an interactive terminal, mirroring the existing diff guard, with an explicit
  opt-out for redirected output or a forcing option.

## Impact
- `Vara.Application.History.SnapshotHistoryService.ShowVersion` (reuses the existing `LooksBinary`
  sampling helper already used by `OpenVersionsForDiff`).
- `Vara.Cli.Commands.ShowCommand` gains a `--force-binary` option and passes whether standard
  output is redirected through to `ShowVersion`.
- A new exception type analogous to `DiffBinaryContentException` for the single-stream `show` case.
- No change to `show` for text content, or for binary content when output is redirected or forced.
