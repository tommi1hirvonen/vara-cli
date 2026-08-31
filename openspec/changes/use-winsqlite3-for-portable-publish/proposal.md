## Why

`vara.exe` is published as a Native AOT executable so it can be copied around as a single portable file, but `Microsoft.Data.Sqlite`'s default `SQLitePCLRaw.bundle_e_sqlite3` bundle deploys a separate native `e_sqlite3.dll` next to the executable. If that DLL is missing, the CLI fails to run, defeating the "just one exe" goal. Windows 10 (1903+) and Windows 11 already ship a system SQLite library (`winsqlite3.dll`), so the CLI can use it instead of carrying its own copy.

## What Changes

- Swap `Vara.Infrastructure`'s SQLite dependency from `Microsoft.Data.Sqlite` (which pulls in `SQLitePCLRaw.bundle_e_sqlite3`) to `Microsoft.Data.Sqlite.Core` + `SQLitePCLRaw.bundle_winsqlite3`, so it P/Invokes the OS-provided `winsqlite3.dll` instead of shipping a native SQLite binary.
- **BREAKING**: Raises the CLI's effective minimum OS requirement to Windows 10 version 1903+ / Windows 11 / Windows Server 2022+ (whichever ships `winsqlite3.dll`). Older Windows versions, and any non-Windows OS, can no longer run `vara.exe`.
- Document the new minimum-OS requirement and the resulting single-file portability guarantee.
- No source code changes to `SqliteSnapshotRepository` or any other class are expected; both bundles self-initialize the same way through `Microsoft.Data.Sqlite`.

## Capabilities

### New Capabilities
- `portable-distribution`: requirement that the published `vara.exe` runs standalone (no companion native library files) on supported Windows versions.

### Modified Capabilities
(none - no other capability's externally observable behavior changes)

## Impact

- `src/Vara.Infrastructure/Vara.Infrastructure.csproj`: package reference swap.
- `Directory.Packages.props`: replace the `Microsoft.Data.Sqlite` version pin with `Microsoft.Data.Sqlite.Core` and add `SQLitePCLRaw.bundle_winsqlite3`.
- Build/publish output: `e_sqlite3.dll` (and its runtime-specific subfolder) no longer appears in the publish directory.
- Documentation: minimum supported Windows version must be called out (new constraint that didn't exist before).
- Tests that exercise `SqliteSnapshotRepository` against a real SQLite database (unit and integration) now depend on `winsqlite3.dll` being present on the machine/CI agent running them (true for all supported Windows dev/CI environments observed in this repo).
