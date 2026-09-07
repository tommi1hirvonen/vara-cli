## Context

See proposal.md - Why. `ErrorReporting.Run(Action, IAnsiConsole?)` returns `0` for
any action that completes without throwing, and `1`/friendly-message path for
recognized/unrecognized exceptions (`src/Vara.Cli/Composition/ErrorReporting.cs`).
`BackupCommand.Create`'s `SetAction` lambda passes its whole run (live-display or
plain-output path, plus `BackupOutcomeReporter.Report`) as that action; a partial
failure (`BackupRunResult.FailedPaths.Count > 0`) never throws, so `ErrorReporting.Run`
always returns `0` for it. `PruneCommand` already demonstrates the pattern this fix
follows: it captures `ErrorReporting.Run`'s return into a local, and its `SetAction`
lambda ends with `return exitCode;` (with one local override, for a user-cancelled
prune) rather than letting `ErrorReporting.Run`'s return flow out directly.

## Goals / Non-Goals

**Goals:**
- `backup` exits a distinct, non-zero, documented code when it completes with one or
  more failed paths, without changing when/how those failures are reported on screen.
- Establish the exit-code value as a small, explicit, shared constant so future
  partial-failure-capable commands reuse it instead of picking their own number.

**Non-Goals:**
- No change to `BackupOutcomeReporter`'s rendering, `BackupRunResult`, or any
  Application-layer failure-recording behavior - `FailedPaths` already exists and is
  already reported; only what the process returns to the OS changes.
- No change to `prune`, `restore`, `diff`, `history`, `snapshots`, `browse`, or
  `deleted` - confirmed in the proposal's audit that none has an equivalent gap today.
- Not defining exit codes for hypothetical future outcome kinds beyond the three the
  `cli-presentation` capability already names (success, partial failure, hard error).

## Decisions

**Exit code value: `2` for partial failure.** `0` (success) and `1` (hard error,
`ErrorReporting.Run`'s existing catch blocks) are already established; `2` is the
next unused value and is the conventional "something's off, but the process itself
ran" code used by many Unix tools (distinct from `1`'s "outright failure"). Considered
reusing `1` for both partial failure and hard error: rejected because it's exactly the
distinction this fix exists to introduce - collapsing them back together would make
the fix a no-op for any caller keying off numeric exit code alone (`$LASTEXITCODE`,
`$?`, `%ERRORLEVEL%`).

**Where the constant lives: a small `internal static class ExitCodes` in
`Vara.Cli.Composition`** (alongside `ErrorReporting`), with named members
(`Success = 0`, `HardError = 1`, `PartialFailure = 2`). Considered adding the value as
a literal directly in `BackupCommand`: rejected because `ErrorReporting.Run`'s own
`1` is also a magic literal today, and a named, shared location is what lets a future
command reuse `PartialFailure` instead of re-deriving the value. Considered making
`ErrorReporting.Run` itself aware of a "partial failure" predicate (e.g. an overload
taking a `Func<bool> hasPartialFailure`): rejected - `ErrorReporting.Run`'s contract is
"translate exceptions," and folding in a non-exception-based outcome would couple it
to a concept (`BackupRunResult.FailedPaths`) it has no other reason to know about.
Keeping the mapping in `BackupCommand` itself (which already has the typed
`BackupRunResult`) is simpler and keeps `ErrorReporting` unchanged.

**How `BackupCommand` gets the result out:** capture `BackupRunResult` in a local
declared before the `ErrorReporting.Run(...)` call (mirroring `PruneCommand`'s
existing `cancelled` local), assign it inside the action, and after `ErrorReporting.Run`
returns, override its result only when it was `Success` (`0`) and
`result.FailedPaths.Count > 0`. Only overriding on `0` (never on `1`) preserves
`ErrorReporting.Run`'s existing hard-error precedence unconditionally - a hard error
always wins over a partial-failure result, since a hard error mid-run typically means
`result` was never assigned (or reflects a run that didn't finish) and should not be
downgraded to the partial-failure code.

## Risks / Trade-offs

- [A caller that only checks "exit code is non-zero" for failure] → No behavior
  change for that caller: partial failure was already reported on screen, and it now
  also exits non-zero (previously `0`, always treated as success). This mitigates the
  bug rather than introducing a new risk.
- [A caller hard-codes "exit code 1 means failure" and ignores `2`] → Documented in
  the exit-code constants' doc comments and in this design; matches the proposal's
  explicit goal of making the two failure kinds distinguishable, which requires
  callers that care about the distinction to check for it.

## Migration Plan

No data or config migration. Purely a CLI process exit-code change, gated behind
existing, already-shipped `BackupRunResult.FailedPaths` reporting. No rollback
concerns beyond a normal revert.
