## 1. Exit code constants

- [ ] 1.1 Add an `internal static class ExitCodes` in `Vara.Cli.Composition` (new file
      `src/Vara.Cli/Composition/ExitCodes.cs`) with named `const int` members
      `Success = 0`, `HardError = 1`, `PartialFailure = 2`, each with a short doc
      comment stating what it means. Verify by building the project
      (`dotnet build`).
- [ ] 1.2 Replace `ErrorReporting.Run`'s two literal `return 1;` statements (the
      recognized-friendly-message catch and the catch-all) with `return
      ExitCodes.HardError;`, and its `return 0;` with `return ExitCodes.Success;`, with
      no behavior change. Verify the existing `ErrorReportingTests` in
      `tests/Vara.Cli.Tests/Composition/ErrorReportingTests.cs` still pass unchanged.

## 2. Wire BackupCommand to the partial-failure exit code

- [ ] 2.1 In `BackupCommand.Create`'s `SetAction` lambda
      (`src/Vara.Cli/Commands/BackupCommand.cs`), declare a `BackupRunResult result =
      null!;` local before the `ErrorReporting.Run(...)` call (mirroring
      `PruneCommand`'s existing `cancelled` local pattern), assign it from the
      existing `RunWithLiveDisplay`/`RunWithPlainOutput` call inside the action, and
      capture `ErrorReporting.Run`'s return value into a local `exitCode` instead of
      returning it directly.
- [ ] 2.2 After the `ErrorReporting.Run(...)` call, override `exitCode` to
      `ExitCodes.PartialFailure` only when `exitCode == ExitCodes.Success &&
      result.FailedPaths.Count > 0`, then `return exitCode;` from the lambda -
      preserving `ExitCodes.HardError` unconditionally when `ErrorReporting.Run`
      already returned it. Verify by building the project (`dotnet build`).
- [ ] 2.3 Add a test in `tests/Vara.Cli.Tests/Commands/BackupCommandTests.cs`
      asserting that invoking the `backup` command against a fake pipeline/profile
      setup that yields a `BackupRunResult` with a non-empty `FailedPaths` returns
      `ExitCodes.PartialFailure` from `SetAction`, and a second test asserting a
      `BackupRunResult` with empty `FailedPaths` still returns `ExitCodes.Success`.
      Verify both tests pass (`dotnet test --filter
      FullyQualifiedName~BackupCommandTests`).
- [ ] 2.4 Add a test verifying a hard error raised before `result` is ever assigned
      (for example, `profileResolver.Resolve` throwing a recognized exception like
      `ProfileConfigException`) still returns `ExitCodes.HardError`, not
      `ExitCodes.PartialFailure` - guarding against the override in 2.2 accidentally
      firing on an unassigned/default `result`. Verify the test passes.

## 3. Full verification

- [ ] 3.1 Run the full test suite (`dotnet test`) and confirm no regressions,
      particularly in `Vara.Cli.Tests` (`ErrorReportingTests`, `BackupCommandTests`,
      `BackupOutcomeReporterTests`) and any integration tests exercising `backup`'s
      exit code.
- [ ] 3.2 Manually run `vara backup --profile <profile>` against a profile with a
      locked/permission-denied file and confirm: the on-screen amber "partial
      failure" summary is unchanged, and the process's exit code
      (`$LASTEXITCODE`/`echo %ERRORLEVEL%`) is `2`. Then run it again with no failing
      files and confirm the exit code is `0`.
