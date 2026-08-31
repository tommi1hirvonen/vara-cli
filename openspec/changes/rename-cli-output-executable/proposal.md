## Why

The published CLI executable currently produces `Vara.Cli.exe`, matching the
`Vara.Cli` project name. The plan is to invoke this tool directly from a
terminal (e.g. `vara backup run`), so the published binary should be named
`vara.exe` — short, lowercase, and convenient to type — without renaming the
project itself.

## What Changes

- Set the `Vara.Cli` project's build/publish output assembly name to `vara`,
  so `dotnet publish` produces `vara.exe` (Windows) / `vara` (other
  platforms) instead of `Vara.Cli.exe` / `Vara.Cli`.
- The project name, namespace, solution references, and folder structure
  (`src/Vara.Cli/...`) remain unchanged — only the compiled/published output
  file name changes.
- Update any docs, scripts, or README instructions that reference invoking
  `Vara.Cli.exe` to instead reference `vara.exe` / `vara`.

## Capabilities

### New Capabilities
None.

### Modified Capabilities
None. This is a build/publish configuration change (output artifact naming)
with no change to application behavior or domain requirements; no existing
capability spec describes build output naming, so no spec deltas apply.
This change sets `skip_specs: true`.

## Impact

- **Affected code**: `src/Vara.Cli/Vara.Cli.csproj` (add `AssemblyName`
  property).
- **Affected docs**: Any README/usage docs referencing `Vara.Cli.exe` as the
  invocation command.
- **Build/publish artifacts**: Published output file name changes from
  `Vara.Cli.exe`/`Vara.Cli` to `vara.exe`/`vara`. Any existing install
  scripts, shortcuts, PATH entries, or CI artifacts that hardcode the old
  file name must be updated to the new name.
- **No breaking change to project structure**: project name `Vara.Cli`,
  namespaces, and references are unaffected.
