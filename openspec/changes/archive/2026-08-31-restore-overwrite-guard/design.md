## Context

`RestoreCommand` (`src/Vara.Cli/Commands/RestoreCommand.cs`) resolves the profile, constructs a
`SnapshotHistoryService`, and calls `RestoreAsOf`/`RestoreVersion`, which both end at
`IContentStore.ExtractTo` (`src/Vara.Core/Abstractions/IContentStore.cs`). The only implementation,
`FileSystemContentStore.ExtractTo` (`src/Vara.Infrastructure/Storage/FileSystemContentStore.cs`),
calls `File.Copy(blobPath, destinationAbsolutePath, overwrite: true)` unconditionally - no
existence check, no containment check. `FileSystemContentStore` already owns `_mirrorRoot`
(the same value as `Profile.TargetRoot`), set in its constructor - see proposal.md - Why.

The existing domain-exception pattern (`NoHistoryForPathException`, `NoMatchingVersionException`
in `src/Vara.Core/Snapshots/SnapshotExceptions.cs`) has `SnapshotHistoryService` itself check
conditions and throw, rather than delegating that decision to `ISnapshotRepository`/`IContentStore`
- those stay simple data/IO accessors. `ErrorReporting.Run` (`src/Vara.Cli/Composition/
ErrorReporting.cs`) maps specific exception types to a friendly `Error: ...` message and exit
code 1; anything else propagates as an unhandled exception. This change follows both patterns.

## Goals / Non-Goals

**Goals:**
- Guard both unsafe cases identified in proposal.md (mirror containment, silent overwrite)
  entirely within the existing layering: `IContentStore` answers filesystem-state questions,
  `SnapshotHistoryService` owns the domain decision and throws, `RestoreCommand` owns the
  `--force`/prompt UX.
- Keep `IContentStore`'s existing members and their behavior unchanged; only add new ones.
- Make the two new failure modes testable through `SnapshotHistoryServiceTests` using the
  existing in-memory `FakeContentStore`, without needing real files on disk for the
  mirror-containment case.

**Non-Goals:**
- Detecting a destination path that *aliases* the mirror via symlinks/junctions or is on a
  different volume mounted at the same logical path. Containment is a straightforward resolved
  path-prefix check against `TargetRoot`, matching the level of rigor already used elsewhere in
  this codebase (e.g. no existing symlink-aware path handling).
- Any change to `--at`/`--version` semantics, snapshot listing, or file-history listing - this is
  scoped entirely to the write side of `restore`.
- A `Vara.Cli.Tests` project. There isn't one today (System.CommandLine wiring and
  `Console.ReadLine`-based prompts aren't currently unit-tested at the CLI layer); this change
  keeps all new decision logic in `SnapshotHistoryService`, which is already covered by
  `Vara.Application.Tests`, and keeps `RestoreCommand`'s own new code (option parsing, the
  `Console.IsInputRedirected` check, and the prompt) as thin, manually-verifiable glue consistent
  with the rest of that file.

## Decisions

**Two new `IContentStore` members, not one combined check.** Add `bool IsWithinMirror(string
absolutePath)` and `bool TargetExists(string absolutePath)`. Alternative considered: a single
`ValidateRestoreDestination(string path)` that throws directly from the content store. Rejected
because it would move the domain-exception-throwing responsibility out of
`SnapshotHistoryService`, breaking the established pattern where the service, not its
dependencies, decides what's an error. Keeping them as two small, independently testable
predicates also means each maps to exactly one new requirement in the spec delta.

**`RestoreAsOf`/`RestoreVersion` gain an `overwrite` parameter (default `false`).** Signature
becomes `RestoreAsOf(string relativePath, DateTimeOffset asOf, string destinationPath, bool
overwrite = false)` (and the equivalent for `RestoreVersion`). The mirror-containment check runs
unconditionally, before the overwrite check, and ignores the `overwrite` flag entirely - it is not
force-able (per the user's explicit decision). The existing-file check only applies when
`overwrite` is `false`. This keeps a single call path for both the "user already confirmed" and
"let me find out if confirmation is needed" cases - `RestoreCommand` decides *when* to pass
`overwrite: true`, but the service is still what enforces it.

**`RestoreCommand` attempts first, then catches and retries, rather than pre-checking existence
itself.** `TargetExists`/`IsWithinMirror` live behind `IContentStore`, which is only constructed
inside `serviceFactory.CreateFor` - not otherwise reachable from `RestoreCommand`. Rather than
threading a new predicate out to the CLI layer just for a pre-check, `RestoreCommand`'s sequence
is: resolve `outPath` -> if `--force`, call the service with `overwrite: true` directly ->
otherwise, call the service with `overwrite: false` first. If that throws
`DestinationExistsException`, check `Console.IsInputRedirected`: non-interactive -> let
`ErrorReporting` report the exception normally (exit 1); interactive -> prompt on `Console.Error`,
and if confirmed, re-invoke the same service call with `overwrite: true`. This costs one harmless
extra call when a prompt-then-confirm path is taken (it fails fast, before any bytes are copied),
in exchange for not exposing content-store internals to the CLI layer. Alternative considered:
expose `TargetExists` through `ProfileServices` so `RestoreCommand` can pre-check without a
throwaway call. Rejected as unnecessary indirection for a single call site.

**Prompt is written to `Console.Error`, gated on `Console.IsInputRedirected` alone.** Per the
user's explicit decision: checking only stdin (not also stdout) means a prompt still works when
stdout is redirected/piped (e.g. `restore ... --out x > log.txt`), since the confirmation text
still needs somewhere visible to land - stderr, not the redirected stdout.

**Decline exits 0.** `RestoreCommand` catches the user's negative/empty answer itself (this is
CLI-layer UX, not a domain exception) and writes a cancellation message, returning `0` from the
command action - per the user's explicit decision that a deliberate no-op isn't a failure.

**Mirror-containment uses `Path.GetFullPath` + ordinal, case-insensitive prefix comparison**
against `Path.GetFullPath(TargetRoot)`, with a trailing separator appended to the mirror root
before comparing so `TargetRootX` doesn't false-positive against a mirror root of `TargetRoot`.
This matches the precision already used for path handling elsewhere in `FileSystemContentStore`
(e.g. `BlobPath`/mirror-relative path joins), which assumes Windows-style, case-insensitive paths
throughout - consistent with this being a Windows-targeted CLI today.

## Risks / Trade-offs

- [`RestoreCommand`'s "attempt with `overwrite: false`, catch, prompt, retry with `overwrite:
  true`" flow means a confirmed overwrite runs the version/date lookup and content extraction
  twice] → Acceptable: restore operates on a single file's content, already read from a
  local content-addressed store; the lookup is not expensive enough to justify adding a
  dedicated "check only" method to avoid one extra call.
- [Path-prefix containment check does not follow symlinks/junctions, so a destination that is
  *outside* `TargetRoot` on paper but reachable back into it via a symlink would not be caught]
  → Accepted as a non-goal; no existing code in this project accounts for symlink aliasing, so
  this change does not introduce a new gap relative to today's baseline.
- [Adding an `overwrite` parameter to `RestoreAsOf`/`RestoreVersion` changes their signatures]
  → Both are internal to `Vara.Application`, called only from `RestoreCommand`; the default
  parameter value means no other call site (there are none today) breaks.

## Migration Plan

No data migration needed - this only changes in-process validation before writes. The externally
visible, intentional breaking change is that a script relying on silent overwrite must now add
`--force`; this is called out in proposal.md as **BREAKING** and needs no code migration, only a
one-time flag addition for any existing automation.
