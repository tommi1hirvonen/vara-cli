## 1. Build Configuration

- [ ] 1.1 Add an `<AssemblyName>vara</AssemblyName>` property to `src/Vara.Cli/Vara.Cli.csproj`, leaving `OutputType`, `PublishAot`, project references, and package references unchanged; verify by inspecting the csproj diff shows only the added property.
- [ ] 1.2 Run `dotnet build src/Vara.Cli/Vara.Cli.csproj` and verify it succeeds and produces `vara.dll` (and `vara.exe` on Windows) under `bin/<config>/<tfm>/`.

## 2. Publish Verification

- [ ] 2.1 Run `dotnet publish src/Vara.Cli/Vara.Cli.csproj -c Release -r win-x64 --self-contained` (or the project's documented publish command/RID) and verify the publish output directory contains `vara.exe` and no longer contains `Vara.Cli.exe`.
- [ ] 2.2 Execute the published `vara.exe` directly (e.g. `vara.exe --help`) and verify it runs and prints CLI help/usage output identically to the previous `Vara.Cli.exe` behavior.

## 3. Regression Check

- [ ] 3.1 Run the existing test suite (`dotnet test`) and verify all tests still pass, confirming the rename did not affect application behavior, namespaces, or references.
- [ ] 3.2 Search the repository for any remaining references to `Vara.Cli.exe` in docs, scripts, or CI configuration and update them to `vara.exe`; verify via a repository-wide search returning no remaining matches outside archived history.
