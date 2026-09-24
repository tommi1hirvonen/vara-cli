## Context

In `src/Vara.Cli/Commands/`, `BackupCommand`, `PruneCommand`, `CheckCommand`, and `SnapshotsCommand` currently define `--profile` as a `System.CommandLine.Option<string>` (or `Option<string?>`). This was previously standardized across all commands when `browse-and-restore-ux` introduced working-directory auto-detection for path-targeting commands (`history`, `restore`, `browse`, `deleted`, `show`, `diff`).

However, unlike path-targeting commands, `backup`, `prune`, and `check` do not accept a file or directory path argument. `snapshots` also does not take a path argument. Because these commands have no other positional arguments, they can accept `profile` as a positional argument without any argument-binding collision.

## Goals / Non-Goals

**Goals:**
- Change `backup`, `prune`, and `check` to require a positional `profile` argument (`vara backup <profile>`, `vara prune <profile>`, `vara check <profile>`).
- Change `snapshots` to accept an optional positional `profile` argument (`vara snapshots [profile]`), falling back to working-directory auto-detection when omitted.
- Disallow `--profile` on these four commands (strict positional).
- Preserve existing argument and option signatures on all path-targeting commands (`history`, `restore`, `show`, `diff`, `browse`, `deleted`).
- Update all associated CLI unit tests and documentation (`README.md`).

**Non-Goals:**
- Supporting dual invocation (`--profile` as an optional alias alongside positional `<profile>`) on `backup`, `prune`, `check`, or `snapshots`.
- Changing any path-targeting command syntax.
- Changing `ProfileResolver`, profile configuration, manifest storage, or execution pipelines.

## Decisions

### Decision: Strict positional arguments via `System.CommandLine.Argument<T>`
For `BackupCommand`, `PruneCommand`, and `CheckCommand`, replace:
```csharp
var profileOption = new Option<string>("--profile") { Description = "...", Required = true };
```
with:
```csharp
var profileArgument = new Argument<string>("profile") { Description = "..." };
```
For `SnapshotsCommand`, replace:
```csharp
var profileOption = new Option<string?>("--profile") { Description = "..." };
```
with:
```csharp
var profileArgument = new Argument<string?>("profile")
{
    Description = "The profile whose snapshots to list. Optional when the current directory is inside a profile's target root.",
    Arity = ArgumentArity.ZeroOrOne,
};
```
And retrieve the value via `parseResult.GetValue(profileArgument)`.

*Rationale*:
- Standard `System.CommandLine` idiom.
- Clean help output and error messages (e.g. missing required argument `profile` when omitted for `backup`).
- Strict syntax ensures a single canonical invocation pattern.

*Alternatives considered*:
- *Dual syntax (accepting both `<profile>` argument and `--profile` option)*: Considered and rejected per explicit design decision. Since Vara has no external users yet, there is no legacy script migration burden, and strict positional syntax keeps help text concise and avoids validation code for conflicting inputs.

### Decision: Retain `--profile` option on path-targeting commands
Commands that operate on files or directories (`history`, `restore`, `show`, `diff`, `browse`, `deleted`) will continue to use `<path>` (or `[directory]`) as their positional argument and `--profile` as an option.

*Rationale*:
- Avoids the `System.CommandLine` positional argument collision where omitting an optional profile argument causes the path token to bind to profile.
- Keeps working-directory auto-detection clean: `cd` into a mirror target and run `vara history file.txt` without naming the profile.
- Clarifies the conceptual model: the positional argument is always the primary entity being operated on (the profile itself for profile-level commands; the file or directory path for file-level commands).

## Risks / Trade-offs

- [Breaking CLI syntax change for `backup`, `prune`, `check`, and `snapshots`] → Mitigated by updating all unit tests in `Vara.Cli.Tests` and the usage documentation in `README.md`.
- [Different profile argument styles across commands (positional on `backup`, option on `restore`)] → Mitigated by the clear semantic distinction: whole-profile commands target the profile directly; file/directory commands target a path within a profile context.

## Migration Plan

No database or config file changes are required. Update:
1. `src/Vara.Cli/Commands/BackupCommand.cs`
2. `src/Vara.Cli/Commands/PruneCommand.cs`
3. `src/Vara.Cli/Commands/CheckCommand.cs`
4. `src/Vara.Cli/Commands/SnapshotsCommand.cs`
5. `tests/Vara.Cli.Tests/Commands/BackupCommandTests.cs`
6. `tests/Vara.Cli.Tests/Commands/PruneCommandTests.cs`
7. `tests/Vara.Cli.Tests/Commands/CheckCommandTests.cs`
8. `README.md`
