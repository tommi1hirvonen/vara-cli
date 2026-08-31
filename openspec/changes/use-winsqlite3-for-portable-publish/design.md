## Context

`Vara.Infrastructure` currently references `Microsoft.Data.Sqlite`, which transitively brings in `SQLitePCLRaw.bundle_e_sqlite3` (pinned at `Microsoft.Data.Sqlite` 10.0.11 in `Directory.Packages.props`). That bundle deploys a native `e_sqlite3.dll` next to `vara.exe` at publish time; `SqliteSnapshotRepository` opens it via a plain `new SqliteConnection($"Data Source={path}")` with no custom provider wiring. See proposal.md - Why for the portability problem this causes.

`SQLitePCLRaw` also ships `SQLitePCLRaw.bundle_winsqlite3`, which P/Invokes `winsqlite3.dll` - the system SQLite library included in Windows 10 1903+, Windows 11, and Windows Server 2022+ - instead of shipping a native binary. Per Microsoft's docs, bundles are auto-initialized by `Microsoft.Data.Sqlite`, so switching bundles requires no call-site changes.

## Goals / Non-Goals

**Goals:**
- Eliminate `e_sqlite3.dll` (and its native runtime subfolder) from the publish output entirely.
- Keep the change to a dependency swap - no changes to `SqliteSnapshotRepository` or any other application code.
- Preserve the existing SQLite database file format/schema (no data migration).

**Non-Goals:**
- Cross-platform (Linux/macOS) portability - this design is Windows-only, per the chosen option.
- Controlling or pinning the exact SQLite version used at runtime - it becomes whatever `winsqlite3.dll` the OS ships.
- Static-linking a native SQLite build into the AOT binary, or embedding/extracting `e_sqlite3.dll` at runtime - both were considered (see Decisions) and rejected in favor of the simpler OS-provided-library approach.

## Decisions

**Use `SQLitePCLRaw.bundle_winsqlite3` via `Microsoft.Data.Sqlite.Core`, instead of the default `Microsoft.Data.Sqlite` + `bundle_e_sqlite3`.**
- In `Vara.Infrastructure.csproj`, replace the `Microsoft.Data.Sqlite` package reference with `Microsoft.Data.Sqlite.Core` and add `SQLitePCLRaw.bundle_winsqlite3`.
- In `Directory.Packages.props`, replace the `Microsoft.Data.Sqlite` version pin (10.0.11) with a `Microsoft.Data.Sqlite.Core` pin at the same version line, and add a `SQLitePCLRaw.bundle_winsqlite3` pin (2.1.11, latest at time of writing).
- No source changes: both bundles are auto-initialized by `Microsoft.Data.Sqlite`'s static constructor, so `SqliteSnapshotRepository` needs no changes to its `SqliteConnection` construction.

**Alternatives considered (from prior exploration) and why they were not chosen:**
- *Static-link a custom-built SQLite `.lib` into the AOT binary* (via `<DirectPInvoke>`/`<NativeLibrary>`): would give full control over the SQLite version and stay cross-platform, but `SQLitePCLRaw` doesn't ship a static library - it would require vendoring and building the SQLite amalgamation per RID and maintaining a custom provider. Rejected as disproportionate effort for the current need.
- *Embed `e_sqlite3.dll` as a resource and extract-and-load at startup* (via `SQLitePCLRaw.provider.dynamic_cdecl`): keeps a single distributable file across all OSes, but adds a runtime bootstrap step (temp-file extraction, custom `IGetFunctionPointer` adapter) and bypasses Native AOT's direct-P/Invoke path. Rejected as more complexity than the Windows-only OS-provided library needs.
- *Replace SQLite with a pure-managed store (e.g. LiteDB) behind `ISnapshotRepository`*: removes the native dependency on any OS, but is a real rewrite of `SqliteSnapshotRepository`'s transactional batch-write and time-based query logic, and gives up SQLite's WAL-based crash safety. Rejected as out of scope for solving a packaging problem.

## Risks / Trade-offs

- [Raises the effective minimum OS to Windows 10 1903+ / Windows 11 / Server 2022+] -> Mitigation: document this prominently (README/release notes) since it is a new hard constraint that did not exist when `e_sqlite3.dll` was bundled.
- [SQLite version is now whatever the OS ships, not a version this project controls or can bump independently] -> Mitigation: `ISnapshotRepository`'s current usage (plain tables/indexes, no FTS/JSON1/R*Tree) does not depend on any recent SQLite feature; note this constraint in developer docs so future SQL usage stays within what an older OS-bundled SQLite supports.
- [Dev machines/CI agents that build or run tests against a real `SqliteSnapshotRepository` must themselves be on a supported Windows version] -> Mitigation: no CI pipeline currently exists in this repo, and local development already happens on a supported Windows version; call this out as a documented environment requirement.

## Migration Plan

1. Update `Directory.Packages.props` and `Vara.Infrastructure.csproj` package references.
2. Run `dotnet build` and the existing unit/integration test suites unchanged, to confirm `SqliteSnapshotRepository` behaves identically against `winsqlite3.dll`.
3. Publish the AOT executable and confirm the output directory no longer contains `e_sqlite3.dll` or its runtime subfolder.
4. Update documentation to state the new minimum-OS requirement.

Rollback: revert the package reference changes; no data or schema migration is involved in either direction since the on-disk SQLite database format is unaffected.
