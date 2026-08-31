## 1. Dependency swap

- [x] 1.1 In `Directory.Packages.props`, replace the `Microsoft.Data.Sqlite` version pin with a `Microsoft.Data.Sqlite.Core` pin (same version line) and verify `dotnet restore` resolves it without errors
- [x] 1.2 In `Directory.Packages.props`, add a `SQLitePCLRaw.bundle_winsqlite3` version pin and verify it is present in the restored package graph (`dotnet list package`)
- [x] 1.3 In `src/Vara.Infrastructure/Vara.Infrastructure.csproj`, replace the `Microsoft.Data.Sqlite` package reference with `Microsoft.Data.Sqlite.Core` and add `SQLitePCLRaw.bundle_winsqlite3`, and verify the solution builds (`dotnet build`)

## 2. Verify behavior is unchanged

- [x] 2.1 Run the existing `Vara.Infrastructure.Tests` suite (including `SqliteSnapshotRepositoryTests`) and verify all tests pass unchanged
- [x] 2.2 Run the existing `Vara.IntegrationTests` suite (including `BackupPipelineRealRepositoryTests`, `SnapshotHistoryServiceRealPortsTests`, `PruneServiceRealPortsTests`) and verify all tests pass unchanged

## 3. Verify the portability goal

- [x] 3.1 Publish `Vara.Cli` as Native AOT (`dotnet publish -c Release`) and verify the output directory contains `vara.exe` with no `e_sqlite3.dll` file or native runtime subfolder
- [x] 3.2 Copy only `vara.exe` to an empty directory and run a command that exercises the snapshot repository (e.g. a backup run against a small test folder) and verify it completes successfully with no missing-DLL error

## 4. Documentation

- [x] 4.1 Add or update project documentation to state the new minimum OS requirement (Windows 10 version 1903+ / Windows 11 / Windows Server 2022+) needed for the OS-provided `winsqlite3.dll`, and verify the note is discoverable from the repo's existing docs entry point
