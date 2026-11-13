# Vara

Vara is a Windows command-line backup tool that keeps a **literal, browsable, one-to-one
mirror** of your files on a target drive, while transparently retaining full version
history underneath - deduplicated and space-efficient - so nothing you overwrite or
delete is ever really gone until you decide to prune it.

```
vara backup files
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
  In Windows Terminal 1.6 or later, live backup, restore, check, and prune progress is
  also reflected in the tab and taskbar; indeterminate scan/evaluation phases are shown
  until a percentage is available. Windows taskbar animation also requires "Show
  animations in Windows" to be enabled.

## Architecture and technology

Vara is written in **F#**, as three projects with a strict dependency direction
(`Cli` → `Engine` → `Domain`). Behavior is functions in modules and data is records and
unions; there are no interfaces, service classes or dependency-injection container.

| Project | Role |
|---|---|
| `Vara.Domain` | The pure core: the profile and configuration model and its validation, scan, diff and move-planning decisions, run state, retention evaluation, history and integrity queries over manifest rows, and the views that describe a command's output as plain data (`Display`, `Tracker`, the history, prune and check views, the profile editor's state machine). It touches no file system, clock, randomness or console. |
| `Vara.Engine` | The effect shell: the directory scanner, the SQLite manifest (`SqliteManifest`), the content store and hard links, content hashing, the per-profile run lock, the YAML configuration file, and the runs that apply the domain's decisions (`BackupRun`, `RestoreRun`, `PruneRun`, `CheckRun`). Effects are passed in as functions or small records of functions, so a test replaces one with a function that fails. |
| `Vara.Cli` | The command surface: `CliEnv.live` defines the live effects, `Program.main` partially applies `Composition.withCommands` to that environment, and `Composition` passes it to each command factory while building the command tree. `System.CommandLine` defines each subcommand, and `Spectre.Console` renders a `Display` live, as plain text, or as JSON. Spectre.Console types appear only in the renderers, the prompts and the console environment. |

Each project has a mirrored xUnit test project under `tests/` (`Vara.Domain.Tests`,
`Vara.Engine.Tests`, `Vara.Cli.Tests`), plus `Vara.Testing`, which holds the spec-coverage
check and the helpers of the recorded corpus. Tests are named after the scenarios of
[the specifications](openspec/specs/), and a test fails when a scenario has no test.

**Key technology choices, and why:**

- **.NET 11 / Native AOT** (`PublishAot=true`) - `vara.exe` publishes to a single,
  self-contained native executable with fast startup and no separate runtime install,
  in service of the "just copy one .exe" distribution goal.
- **`System.CommandLine`** for argument parsing/routing. The generic host
  (`Microsoft.Extensions.Hosting`) is deliberately not used: its service-oriented defaults
  (loading `appsettings.json` from the working directory, `DOTNET_*` environment variables,
  stdout console logging) don't suit a CLI.
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
- **A hand-written `DllImport` wrapper around Win32 `CreateHardLinkW`** (in
  `Kernel32.fs`) - `File.CreateHardLink` isn't available on the `net11.0` target.
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
dotnet publish src/Vara.Cli -c Release -r win-x64 -o artifacts/cli
```

The publish output (`artifacts/cli`) contains only `vara.exe` (plus optional `.pdb` symbol files) - no
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
| `vara backup <profile> [--dry-run] [--json] [--depth <n>] [--files] [--interactive]` | Run an incremental backup for a profile; `--dry-run` previews planned changes without writing anything; `--json` prints a single machine-readable JSON summary instead of the human-oriented output. `--depth`, `--files` and `--interactive` add the change tree after the summary, the same tree `vara snapshot` shows: for a dry run it shows the planned changes, for a real run the changes the run recorded. `--depth` limits the levels below the mirror root, `--files` lists the changed paths, and `--interactive` (which implies the tree) opens it for keyboard browsing on a terminal, with the file rows, symbols and the `d` and `f` keys described for `vara snapshot`; where input or output is redirected, or color is off, it prints the static tree and notes this on standard error. Browsing never changes the exit code. The tree options cannot be combined with `--json`. |
| `vara snapshots [profile]` | List recorded snapshots (timestamp, stats, outcome). |
| `vara snapshot <id> [directory] [--files] [--depth <n>] [--interactive] [--profile <name>] [--config <path>]` | Show a snapshot's directory change rollups; color distinguishes change kinds. `--files` includes changed paths. `--depth` limits displayed directory levels below the selected scope (`0` shows only that directory; omitted by default for an unlimited tree); hidden affected descendants are counted at the visible boundary. `--interactive` opens the tree for keyboard browsing on a terminal (Up/Down move, Right expands or steps into the first child, Left collapses or goes to the parent, Home/End, `*` expands all, `d` shows or hides deleted files, `f` shows or hides all files, `q` or Esc quits). The interactive tree lists the changed files of every expanded directory, down to the deepest, whether or not `--files` is given, each with a symbol for what happened to it (`+` added, `~` changed, `->` moved, `-` deleted, `@` linked) and a detail line with the path of the row under the cursor; a deleted file that a moved file came from is not listed again, and directory counts are not affected by `d` or `f`, starting at `--depth` levels (default 1); counts always cover the whole subtree. Where input or output is redirected, or color is off, it prints the static tree and notes this on standard error. |
| `vara history <path> [--profile <name>]` | List a file's recorded versions, most recent first. |
| `vara browse [directory] [--profile <name>] [--at <date>] [--deleted]` | List a mirror directory's contents, optionally as of a past date, optionally including deleted entries. |
| `vara deleted [directory] [--profile <name>] [--since <date>]` | Report deleted files, most recently deleted first. |
| `vara restore <path> (--out <dest> \| --in-place) (--at <date> \| --version <id>) [--force]` | Extract a historical version to a destination (or back to its original source location) without touching the live mirror. |
| `vara restore <path> --recursive (--out <dest> \| --in-place) [--at <date> \| --snapshot <id>] [--force]` | Restore an entire directory (subtree) to its exact tracked state as of `--at` or `--snapshot <id>`, or its current tracked state if neither is given non-interactively; in an interactive session, omitting both instead presents a selectable list of candidate snapshots (plus a "current tracked state" entry) with directory-scoped stats. Reports planned writes/removals and asks for one confirmation before applying them unless `--force` is given. Mutually exclusive with `--version`; `--at` and `--snapshot` are mutually exclusive with each other. |
| `vara show <path> (--at <date> \| --version <id>)` | Stream a historical version's content to stdout. |
| `vara diff <path> (--left-at <date> \| --left-version <id>) (--right-at <date> \| --right-version <id>)` | Show a textual diff between two versions of a file. |
| `vara prune <profile> [--yes]` | Apply the profile's tiered retention policy and garbage-collect unreferenced content. |
| `vara check <profile> [--quick]` | Verify content physically stored in the target still matches the manifest across the full snapshot history; reports missing/corrupt/orphaned blobs with verification progress, percentage, and estimated time remaining. Full checks weight progress by distinct blob sizes; `--quick` uses blob counts. |
| `vara profiles [--config <path>]` | Open an interactive terminal UI to create, edit, and delete profiles in the configuration file, instead of hand-editing YAML. Requires an interactive terminal (fails cleanly if input or output is redirected). |

Profile-level commands (`backup`, `prune`, `check`) require the profile as a positional
argument (`vara backup <profile>`, `vara prune <profile>`, `vara check <profile>`), and
`snapshots` takes an optional positional profile (`vara snapshots [profile]`). These
commands do not accept a `--profile` option. When `[profile]` is omitted from `snapshots`,
Vara walks upward from the current directory looking for a `.vara\profile.db` file, the same
way Git locates `.git` - so you can `cd` into a mirror and run `vara snapshots` without
naming the profile.

Path- and directory-targeting commands (`history`, `restore`, `show`, `diff`, `browse`,
`deleted`) keep their path or directory as the positional argument (`<path>` or
`[directory]`) and take `--profile <name>` as an optional option. When `--profile` is
omitted, they also fall back to working-directory auto-detection inside a mirror. This
directory-based resolution works even without `~/.vara/profiles.yml` existing at all (e.g.
against a mirror copied to another machine), and is only consulted when `--profile` is
omitted - an explicit `--profile <name>` always resolves via the configuration file instead,
regardless of the current directory.

A `path` argument to `history`/`restore`/`show`/`diff`/`browse` accepts a mirror-relative
path, an absolute source path (e.g. pasted from Explorer), or a path relative to the
current directory - Vara resolves whichever form matches recorded history.

`restore` and `show` accept either `--version <id>` (from `history`'s output) or
`--at <date>` (the version current as of that date). For single-file `restore`, in an
interactive session, omitting both presents a selectable list of versions instead of
erroring; `show` always requires one of the two and errors if both are omitted. This
single-file picker does not apply to `restore --recursive` - a directory restore has no
single per-file version history to pick from. Instead, `restore --recursive` accepts
`--at <date>` or `--snapshot <id>` (a specific snapshot id, from `snapshots`' output);
in an interactive session, omitting both presents a selectable list of candidate
snapshots that touched the directory - each showing its directory-scoped added/changed/
moved/deleted counts, net byte change, and point-in-time file/byte/symlink totals - plus
a "current tracked state" entry pre-selected as the default, so accepting it without
changing the selection reproduces the same result as omitting both non-interactively.

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

The solution targets **.NET 11** (`net11.0`) and **F# 11**; install the .NET 11 SDK to
build, test, or publish it. Every command below runs from the repository root.

```
dotnet restore Vara.slnx
dotnet build Vara.slnx
dotnet test Vara.slnx
dotnet publish src/Vara.Cli -c Release -r win-x64 -o artifacts/cli
```

The published `artifacts/cli/vara.exe` is the Native AOT build described under
[Installation](#installation). `dotnet build` is expected to report no warnings; the F#
projects treat an incomplete match, a nullness error and a task that is not compiled to a
state machine as errors.

Layout:

```
src/
  Vara.Domain/        pure decisions, data and views
  Vara.Engine/        file system, SQLite, hashing, YAML, the runs
  Vara.Cli/           composition root, commands, rendering (publishes vara.exe)
tests/
  Vara.Domain.Tests/  scenario and property tests of the domain
  Vara.Engine.Tests/  engine, configuration and corpus tests
  Vara.Cli.Tests/     command tests, in process and as processes
  Vara.Testing/       spec-coverage check, corpus and anonymizing helpers
benchmarks/           BenchmarkDotNet scenarios, baselines, release check script
openspec/             specifications and the history of the changes that made them
```

Tests are named after the specification: a module is named after a requirement and a test
after one of its scenarios, and the spec-coverage tests fail when a scenario of any spec in
`openspec/specs` has no test.

**Recorded corpus.** `tests/Vara.Engine.Tests/Corpus` and `tests/Vara.Cli.Tests/Corpus`
hold results that the earlier C# build gave for the configuration loader, the retention
boundaries, the configuration writer, the backup engine and the command line. They are
data (marked `-text` in `.gitattributes`, so their bytes are never converted) and are
compared with the F# results by plain functions. The C# build is no longer in the
repository, so a corpus file is changed only when the behavior it records is changed on
purpose, in the same change as the code.

**Benchmarks.** Run the BenchmarkDotNet scenarios in Release mode:

```
dotnet run -c Release --project benchmarks/Vara.Benchmarks -- --list flat
dotnet run -c Release --project benchmarks/Vara.Benchmarks -- --filter '*StreamingSignatureBenchmarks*'
dotnet run -c Release --project benchmarks/Vara.Benchmarks -- --filter '*' --job short
```

See [the benchmark guide](benchmarks/Vara.Benchmarks/README.md) for scenario coverage and
measurement guidance, and [the recorded baselines](benchmarks/baselines/README.md) for the
reference results. Allocations and CPU time are the figures to trust; wall-clock time on a
developer machine is noisy. `benchmarks/Measure-ReleaseCheck.ps1` is an optional release
check of the published `vara.exe` (CPU time, peak memory and size) against the recorded baseline; see
[the baselines](benchmarks/baselines/README.md) for how to run it.

### Code conventions

The code is idiomatic F#, designed from the specifications. These rules apply to new code:

- **Modules and data, not classes.** Behavior is functions in modules; data is records and
  unions. No interfaces, inheritance, service classes or dependency injection. A class is
  allowed only where .NET requires one: handle wrappers, `IDisposable` resources, streams,
  and types that a host library demands (such as a BenchmarkDotNet scenario).
- **Dependencies are parameters.** A function takes the functions it needs, not an object
  that holds them. Effects that travel together may be grouped as a record of functions,
  when three or more are used together (`Links`, `ExecutionEnv`, `BackupEnv`, `CliEnv`).
  `CliEnv.live` defines the live CLI dependencies; `Program.main` partially applies
  `Composition.withCommands` to that environment, and `Composition` passes it to command
  factories. Invocation-specific dependencies, such as a backup environment tied to the
  command's cancellation token, are created when that command runs.
- **Compose, don't sequence.** Build behavior from small functions joined with `|>`,
  `Result`, `Option` and `Seq` combinators, and a computation expression where a flow
  reads in order. Put the data argument last, so functions can be partially applied when
  useful.
- **Types carry the rules.** Unions for alternatives, `[<RequireQualifiedAccess>]` on
  them, records with named fields, single-case unions or units of measure for values that
  could be confused. Matches on the code's own unions are exhaustive, with no wildcard; no
  `null`; invalid states are unrepresentable.
- **Expected outcomes are values.** Use `Result` or `option` for them. Failures have three
  levels: a refusal to start is a `Result` error, a per-operation I/O failure is a value
  that the run records and continues past, and anything else is an exception that is
  recorded and rethrown with its stack (`ExceptionDispatchInfo` in `task {}`) and handled
  at the host boundary.
- **Pure core, effect shell.** Decisions in `Vara.Domain` never touch the file system, the
  clock, randomness or the console. The shell passes in values and performs the effects.
  A destructive operation is a plan, computed by a pure function, and then an execution.
- **Presentation is a pure view.** A command builds a `Display` (plain data: lines,
  fixed-width cells, tones) in `Vara.Domain`; one interpreter per output (`LiveRenderer`,
  `PlainRenderer`, JSON) writes it. Spectre.Console types appear only in the renderers,
  `Prompts`, `Terminal`, `CliEnv`, `CommandAction` and `ErrorReporting`.
- **Interactive commands are Elm-style loops.** State and input are unions, `update` and
  `view` are pure, and the thin loop around them asks, calls `update` and performs the
  effects it returns (`ProfileEditor`).
- **Mutation stays inside one function.** It is allowed in a hot loop, a pooled buffer or
  interop, when a benchmark or the API requires it, with the reason in the commit message.
  It never lives in a field, is never shared between functions, and does not appear in
  `Vara.Domain`. Allocation rules (struct unions, `voption`) change a value's
  representation, not the architecture.
- **No catch-all modules.** No `Helper`, `Manager`, `Service`, `Util` or `I`-prefixed
  names. One module per concept, with files in dependency order.
- **Native AOT.** `Seq.init` and `Seq.initInfinite` raise IL2091 from FSharp.Core; use an
  array range (`[| 0 .. n - 1 |]`). The publish must report no IL2xxx or IL3xxx warnings.
- **Tests.** Test names follow the specification. A test replaces an effect with a function
  (a scripted prompt, a field of `CliEnv`, a function that raises), never a console type.
  Every SQLite user in a test clears the connection pool when it closes. The tests run in
  parallel; if one of them fails only under load (a SQLite `ObjectDisposedException` on a
  disposed handle is the known case), rerun it alone and report the flake.

This project's specifications and design decisions are tracked with
[OpenSpec](openspec/) under `openspec/specs/` (current, authoritative capability specs)
and `openspec/changes/archive/` (the historical proposals that produced them) - useful
background reading, but the code itself remains the ultimate source of truth for actual
behavior.
