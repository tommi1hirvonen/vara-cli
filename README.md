# Vara

Vara is a Windows command-line backup tool that keeps a **literal, browsable, one-to-one
mirror** of your files on a target drive, while transparently retaining full version
history underneath - deduplicated and space-efficient - so nothing you overwrite or
delete is ever really gone until you decide to prune it.

```
vara backup --profile files
```

runs an incremental backup for a profile named `files`; `vara snapshots`, `vara history`,
`vara browse`, `vara restore`, `vara show`, `vara diff` and `vara prune` let you inspect
and recover that history afterwards, and `vara profiles` opens an interactive editor for
creating, editing, and deleting profiles without hand-editing YAML.

## Table of contents

- [Motivation](#motivation)
- [What it does](#what-it-does)
- [Architecture and technology](#architecture-and-technology)
- [Installation](#installation)
- [Configuration](#configuration)
- [Usage](#usage)
- [How it works](#how-it-works)
- [Strengths and trade-offs](#strengths-and-trade-offs)
- [Development](#development)

## Motivation

Vara was built to solve its author's own recurring problem: backing up a home Windows
machine to an external drive - often one with **hardware-level encryption** - without
losing the ability to recover an earlier version of a file that got overwritten or
deleted by accident. Cloud sync tools optimize for availability across devices, not
point-in-time recovery; image-based backup tools produce opaque archives you can't
browse with Explorer; and running a full deduplicating backup *server* (Borg, restic,
etc.) is overkill for "one PC, one external drive, plugged in occasionally."

Vara instead produces a target folder that **is** a normal, browsable copy of your
files - so in a pinch you can restore your whole machine by literally copying that
folder back - while keeping historical versions and deduplicating identical content
behind the scenes. It intentionally targets a narrow use case (single Windows machine,
local or directly-attached external target, occasional/manual runs) rather than trying
to be a general-purpose, multi-client backup platform.

## What it does

- **Mirrors sources to a target** exactly, one profile at a time - what you see in the
  target folder is what's currently in your sources, no proprietary archive format.
- **Backs up incrementally**: unchanged files cost only a manifest lookup (size +
  modified time), so repeat runs against mostly-unchanged data are fast.
- **Deduplicates content** across files, historical versions, and the live mirror,
  using content hashing and (where the target filesystem supports it) hardlinks.
- **Detects moves and renames** without re-copying bytes, using a cheap bounded-read
  pre-check before falling back to a full content comparison.
- **Retains full version history** for every changed or deleted file, browsable and
  restorable independent of the current mirror state.
- **Prunes history on a tiered schedule** (daily/weekly/monthly/yearly buckets) to keep
  disk usage bounded on smaller external drives, always keeping at least the latest
  snapshot.
- **Degrades gracefully** on filesystems without hardlink support (e.g. exFAT/FAT32):
  backups still work and dedup still applies to the version store, only live-mirror
  byte-sharing is lost.
- **Survives interruption**: crash-safe atomic writes, a run lock per profile, and
  automatic recovery/cleanup on the next run.
- **Reports live progress**: byte-weighted progress bars, throughput and ETA during
  transfers, with a plain-text fallback when output isn't an interactive terminal.

## Architecture and technology

Vara follows a **clean/onion architecture**, split into four projects with a strict
dependency direction (`Cli` → `Application`/`Infrastructure` → `Core`):

| Project | Role |
|---|---|
| `Vara.Core` | Domain model and ports: `Profile`/`Source`/`RetentionPolicy` config types, file-system/snapshot/version records, and interfaces (`IContentStore`, `IFileSystemScanner`, `IHasher`, `ISnapshotRepository`, `IManifestBatch`, `IRunLock`, `IProfileConfigLoader`). No dependency on any concrete storage or I/O technology. |
| `Vara.Application` | Use-case orchestration: the backup pipeline (`Backup`), snapshot/version browsing and restore (`History`), retention evaluation (`Retention`), profile resolution (`Profiles`), and progress-calculation logic (`Reporting`) - all written against `Vara.Core`'s ports, with no knowledge of the CLI or concrete infrastructure. |
| `Vara.Infrastructure` | Concrete implementations: a SQLite-backed manifest and content store (`Storage`, `Snapshots`), a directory-tree filesystem scanner (`FileSystem`), a YAML profile loader (`Configuration`), content hashing (`Hashing`), the per-profile run lock (`Concurrency`), and Win32 P/Invoke interop for hardlink creation (`Interop`). Bounded parallelism for the scan and transfer stages is implemented directly in `Vara.Application` (via .NET's `Parallel.ForEach`), not here. |
| `Vara.Cli` | The composition root and command surface: a `Microsoft.Extensions.Hosting` `HostBuilder` wires ports to implementations, and `System.CommandLine` defines each subcommand (`Commands`), rendered through `Spectre.Console` (`Presentation`). |

Each layer has its own mirrored xUnit test project under `tests/` (`Vara.Core.Tests`,
`Vara.Application.Tests`, `Vara.Infrastructure.Tests`, `Vara.Cli.Tests`), plus
`Vara.IntegrationTests` for cross-layer, real-filesystem scenarios.

**Key technology choices, and why:**

- **.NET 10 / Native AOT** (`PublishAot=true`) - `vara.exe` publishes to a single,
  self-contained native executable with fast startup and no separate runtime install,
  in service of the "just copy one .exe" distribution goal.
- **`System.CommandLine`** for argument parsing/routing, and **`Microsoft.Extensions.Hosting`**
  purely as a lightweight DI container (composition root only - no generic host services
  like logging/config providers are used beyond that).
- **`Microsoft.Data.Sqlite` over `winsqlite3.dll`** (the SQLite library built into
  Windows 10 1903+/11/Server 2022+) instead of `Microsoft.Data.Sqlite`'s usual bundled
  native binary - this keeps the publish output to just `vara.exe`, with no companion
  `e_sqlite3.dll` to place alongside it. EF Core was deliberately **not** used: the
  manifest schema is small and stable, the key queries (latest-version-per-path,
  calendar-bucketed retention, reference-count garbage collection) are naturally
  set-based SQL rather than object-graph-shaped, and avoiding EF Core's reflection-heavy
  surface reduces Native AOT/trimming risk.
- **`System.IO.Hashing` (XxHash128)** for content addressing - fast, non-cryptographic,
  and wide enough that accidental collisions are not a practical concern for
  change-detection/dedup (this is not a security boundary).
- **A hand-written `LibraryImport` P/Invoke wrapper around Win32 `CreateHardLinkW`** -
  `File.CreateHardLink` isn't available on `net10.0`, and `LibraryImport`'s
  source-generated marshalling keeps hardlink creation Native AOT/trimming safe (unlike
  reflection-driven classic `DllImport` stubs).
- **`YamlDotNet`** for the human-editable `~/.vara/profiles.yml` profile format, and
  **`Microsoft.Extensions.FileSystemGlobbing`** for a source's optional glob
  include/exclude patterns.
- **`Spectre.Console`** for tables, progress bars, and interactive prompts, with an
  explicit plain-text fallback path whenever output is redirected or non-interactive.
- **`DiffPlex`** to render textual diffs between two historical versions of a file.

## Installation

There is no installer. `Vara.Cli` is published as a Native AOT executable and is
fully portable: copy the single `vara.exe` file to any location and run it - no
other files or native libraries are required alongside it.

```
dotnet publish src/Vara.Cli/Vara.Cli.csproj -c Release -r win-x64
```

The publish output contains only `vara.exe` (plus optional `.pdb` symbol files) - no
`e_sqlite3.dll` or other native SQLite binary is produced or required, since `vara.exe`
uses the SQLite library built into Windows (`winsqlite3.dll`) instead of shipping its
own copy.

Copy the resulting `vara.exe` anywhere you like (e.g. `C:\Tools\vara\vara.exe`), then
add its folder to your user `PATH` environment variable so it can be invoked as just
`vara` from any shell.

**Minimum OS requirement:** because it relies on `winsqlite3.dll`, `vara.exe` requires
one of:

- Windows 10, version 1903 or later
- Windows 11
- Windows Server 2022 or later

Older Windows versions, and non-Windows operating systems, do not provide
`winsqlite3.dll` and cannot run `vara.exe`.

## Configuration

Vara reads profiles from `~/.vara/profiles.yml` by default (override with `--config`).
See [`docs/config-sample.yml`](docs/config-sample.yml) for a full annotated example:

```yaml
profiles:
  - name: files
    target: 'D:\backup\'
    sources:
      - path: 'C:\Users\user\Downloads\'
        recursive: false
      - path: 'C:\Users\user\Documents\'
        recursive: true
        exclude:
          - 'subfolder1\'
    retention:
      keep_daily: 14
      keep_weekly: 8
      keep_monthly: 12
      keep_yearly: 3
    concurrency:
      scan_concurrency: 8       # default: number of processor cores
      transfer_concurrency: 2   # default: 1 (kept low for slower external drives)
```

Each profile has a name, a target root, and one or more sources (with optional
`recursive`, `exclude`, and glob include/exclude patterns). `retention` is optional -
`vara prune` refuses to run for a profile until it's configured. `concurrency` is
optional and rarely needed.

Instead of hand-editing this file, `vara profiles` opens an interactive terminal UI for
creating, editing, and deleting profiles - see [`vara profiles`](#usage) below.

## Usage

| Command | Purpose |
|---|---|
| `vara backup --profile <name> [--dry-run] [--json]` | Run an incremental backup for a profile; `--dry-run` previews planned changes without writing anything; `--json` prints a single machine-readable JSON summary instead of the human-oriented output. |
| `vara snapshots [--profile <name>]` | List recorded snapshots (timestamp, stats, outcome). |
| `vara history <path> [--profile <name>]` | List a file's recorded versions, most recent first. |
| `vara browse [directory] [--profile <name>] [--at <date>] [--deleted]` | List a mirror directory's contents, optionally as of a past date, optionally including deleted entries. |
| `vara deleted [directory] [--profile <name>] [--since <date>]` | Report deleted files, most recently deleted first. |
| `vara restore <path> (--out <dest> \| --in-place) (--at <date> \| --version <id>) [--force]` | Extract a historical version to a destination (or back to its original source location) without touching the live mirror. |
| `vara restore <path> --recursive (--out <dest> \| --in-place) [--at <date>] [--force]` | Restore an entire directory (subtree) to its exact tracked state as of `--at`, or its current tracked state if `--at` is omitted; reports planned writes/removals and asks for one confirmation before applying them unless `--force` is given. Mutually exclusive with `--version`. |
| `vara show <path> (--at <date> \| --version <id>)` | Stream a historical version's content to stdout. |
| `vara diff <path> (--left-at <date> \| --left-version <id>) (--right-at <date> \| --right-version <id>)` | Show a textual diff between two versions of a file. |
| `vara prune --profile <name> [--yes]` | Apply the profile's tiered retention policy and garbage-collect unreferenced content. |
| `vara check --profile <name> [--quick]` | Verify content physically stored in the target still matches the manifest across the full snapshot history; reports missing/corrupt/orphaned blobs. |
| `vara profiles [--config <path>]` | Open an interactive terminal UI to create, edit, and delete profiles in the configuration file, instead of hand-editing YAML. Requires an interactive terminal (fails cleanly if input or output is redirected). |

The profile is always given via a `--profile` option, never a positional argument -
including for `backup`, which took a positional `<profile>` in earlier versions. This
is deliberate: once path-taking commands (`history`, `restore`, `show`, `diff`) needed
their profile to become optional, `System.CommandLine` could no longer redistribute a
single leftover positional token to the still-required `<path>` argument, so every
command was made consistent by moving the profile to `--profile` instead.

`--profile` is required for `backup` and `prune` (they mutate data) and for `check`
(it always targets one explicit profile rather than falling back to directory-based
resolution), but optional for
the read-only browsing/restore commands (`snapshots`, `history`, `restore`, `show`,
`diff`, `browse`, `deleted`): if omitted, Vara walks upward from the current directory
looking for a `.vara\profile.db` file, the same way Git locates `.git` - so you can `cd`
into a mirror and run `vara snapshots` without naming the profile. This directory-based
resolution works even without `~/.vara/profiles.yml` existing at all (e.g. against a
mirror copied to another machine), and is only consulted when `--profile` is omitted -
an explicit `--profile <name>` always resolves via the configuration file instead,
regardless of the current directory.

A `path` argument to `history`/`restore`/`show`/`diff`/`browse` accepts a mirror-relative
path, an absolute source path (e.g. pasted from Explorer), or a path relative to the
current directory - Vara resolves whichever form matches recorded history.

`restore` and `show` accept either `--version <id>` (from `history`'s output) or
`--at <date>` (the version current as of that date). For single-file `restore`, in an
interactive session, omitting both presents a selectable list of versions instead of
erroring; `show` always requires one of the two and errors if both are omitted. This
picker does not apply to `restore --recursive` - a directory restore has no single
per-file version history to pick from.

`vara profiles` (optionally with `--config <path>`, matching the same option accepted by
the other commands) opens a full-screen interactive editor over the configuration file:
a main menu lists every profile by name, target root, and source count, and offers "Add
new profile" and a per-profile delete action (which asks for an explicit yes/no
confirmation and only removes the profile's configuration entry - it never touches
existing backup data under that profile's target). Selecting a profile, or "Add new
profile", opens an edit screen where the name, target root, sources (each with its own
path, `recursive` flag, and exclude/glob lists), retention policy, and concurrency
settings can be changed in any order; every edit is validated immediately using the same
rules the configuration loader enforces (required fields, absolute paths, non-overlapping
target/sources, non-negative retention counts, positive concurrency values, and no
duplicate profile name), and "Save" only writes the file if the draft is valid at that
moment - "Discard" abandons the draft and returns to the main menu with no change. This
command requires a real interactive terminal (both input and output) and exits with an
error, making no change, if either is redirected.

## How it works

Each profile's target root contains the live mirror directly, plus a `.vara\`
folder:

```
<target>\                    live mirror - real files, or hardlinks into the version
                              store where the target filesystem supports it
  .vara\
    profile.db                SQLite manifest: snapshots + per-path file-version rows
    versions\<xx>\<hash>       content-addressed store, one physical blob per unique
                               content hash (fan-out by hash prefix)
    tmp\                       staging area for atomic writes
```

A mirror path is derived from a source file's **full absolute path** (e.g.
`C:\Users\john\Documents\notes.txt` → `<target>\C\Users\john\Documents\notes.txt`), so
files from multiple configured sources never collide in the mirror, and the mirror
never needs a second, independent addressing scheme.

A `vara backup` run proceeds through five stages:

1. **Scan** - walk configured sources (respecting `recursive`, `exclude`, and glob
   rules), skipping symlinks/junctions (their existence is recorded, not traversed).
2. **Diff** - compare each entry's size and modified time against the manifest's
   current-state view; only unmatched/changed entries are hashed and read.
3. **Plan** - compute the full add/change/move/delete operation set and its total byte
   count *before* any transfer starts, which is what makes an accurate progress
   bar/throughput/ETA possible from the first byte copied.
4. **Execute** - stream, hash, and store each changed file (temp file → content-store
   blob → hardlink-or-copy into the mirror via rename, never an in-place overwrite);
   apply moves as renames; apply deletions by removing the mirror entry only (content
   stays in the version store, referenced by a tombstoned manifest row).
5. **Commit** - mark the snapshot `complete` with final stats; an interrupted run is
   left `running`/reconciled to `failed` and its orphaned temp files are swept up by
   the next run's startup check.

A per-profile lock file prevents two runs from targeting the same profile concurrently.
Hardlink support is re-probed at the start of every run (not cached across runs), since
a removable drive's filesystem can change over time (e.g. reformatted to exFAT).

`vara prune` buckets snapshots per configured retention tier, keeps the newest snapshot
per bucket (always keeping at least the single most recent snapshot regardless of
configuration), deletes manifest rows for anything else - but only rows that aren't a
path's *current* state, so a long-untouched live file is never orphaned - and then
garbage-collects any version-store blob no longer referenced by a remaining row or the
live mirror.

## Strengths and trade-offs

**Strengths, compared to other backup tools:**

- **Zero-install, single-file distribution** - a native AOT `vara.exe` with no runtime,
  no companion DLLs, no installer; copy it anywhere and add it to `PATH`.
- **The backup target is directly browsable** - no proprietary archive/repository
  format to mount or extract (unlike Borg/restic/Duplicati); a disaster-recovery
  restore can literally be "copy the target folder back."
- **Space-efficient version history without a server or dedicated repository
  process** - content-addressed storage and hardlinks give you Time Machine/Borg-style
  deduplication on plain NTFS, with no daemon and no repository-format lock-in.
- **Byte-weighted progress with real ETA** - because the full transfer plan is computed
  up front, progress reflects actual bytes remaining, not naive file/directory counts.
- **Purpose-built for one real scenario** (Windows, external/hardware-encrypted drive,
  occasional manual runs) rather than trying to cover every backup topology, which kept
  its design simple and its edge-case handling (crash safety, locked files, hard-link
  limits, exFAT degradation) concretely testable.

**Accepted trade-offs / weaknesses:**

- **Windows-only, and only modern Windows** (10 1903+/11/Server 2022+) - it depends on
  `winsqlite3.dll`, which isn't present on older Windows or any non-Windows OS. Tools
  like restic or Borg are cross-platform.
- **No encryption or compression of its own** - Vara relies on the target volume
  already providing hardware-level encryption (its primary intended use case); unlike
  restic/Duplicati/Borg, it will not encrypt or compress data itself if the target
  doesn't already do so.
- **No remote/cloud backend** - sources and targets must be locally reachable paths
  (local disks, external drives, mapped network drives); there's no S3/B2/SFTP backend
  the way restic or Duplicacy provide.
- **No file-level ACL, alternate-data-stream, or NTFS permission fidelity**, and
  symlinks/junctions are recorded but not followed - a deliberate scope cut for a
  personal-files use case, but a gap versus image-level or ACL-aware backup tools.
- **No USN Journal or VSS/shadow-copy integration** - change detection is a full
  directory walk diffed against the manifest (size + mtime, not journal-driven), and a
  file locked by another process is skipped and reported rather than snapshotted via
  VSS. This keeps the implementation simple at the cost of missing changes to
  in-use files and doing more I/O on very large trees than a journal-aware scanner
  would.
- **Single-machine, single-operator model** - there's no multi-client repository,
  server component, or concurrent-writer support beyond the per-profile run lock;
  it is not designed as a shared/team backup target.
- **Manual/scheduled invocation only** - Vara has no built-in scheduler or background
  service; recurring backups require an external scheduler (e.g. Windows Task
  Scheduler).

## Development

```
dotnet build Vara.slnx
dotnet test Vara.slnx
```

Tests are split per architectural layer plus one cross-layer suite:
`Vara.Core.Tests`, `Vara.Application.Tests`, `Vara.Infrastructure.Tests`,
`Vara.Cli.Tests`, and `Vara.IntegrationTests`.

This project's specifications and design decisions are tracked with
[OpenSpec](openspec/) under `openspec/specs/` (current, authoritative capability specs)
and `openspec/changes/archive/` (the historical proposals that produced them) - useful
background reading, but the code itself remains the ultimate source of truth for actual
behavior.
