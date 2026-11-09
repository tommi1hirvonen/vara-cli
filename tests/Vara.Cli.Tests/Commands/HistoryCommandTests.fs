/// Tests of the read-only history commands, through the command against a real target written by the F# backup. Each
/// module is named after a requirement of openspec/specs/snapshot-history, backup-browsing or cli-presentation and each
/// test after one of its scenarios.
module Vara.Cli.Tests.HistoryCommandTests

open System
open System.Diagnostics
open System.IO
open System.Threading
open Microsoft.Data.Sqlite
open Spectre.Console.Testing
open Xunit
open Vara.Cli
open Vara.Cli.Tests.BackupCommandTests
open Vara.Domain
open Vara.Engine

/// A write-only stream over another that does not close it, so that what \`show\` streamed can be read afterwards.
type NonClosingStream(inner: Stream) =
    inherit Stream()
    override _.CanRead = false
    override _.CanSeek = false
    override _.CanWrite = true
    override _.Length = raise (NotSupportedException())

    override _.Position
        with get () = raise (NotSupportedException())
        and set _ = raise (NotSupportedException())

    override _.Read(_: byte[], _: int, _: int) : int = raise (NotSupportedException())
    override _.Flush() = inner.Flush()
    override _.Seek(_: int64, _: SeekOrigin) : int64 = raise (NotSupportedException())
    override _.SetLength(_: int64) = raise (NotSupportedException())
    override _.Write(buffer: byte[], offset: int, count: int) = inner.Write(buffer, offset, count)

// ---- helpers shared with the restore tests ------------------------------------------------------------------------

let sourceFile (setup: Scratch) (name: string) : string = Path.Combine(setup.Source, name)

/// The recorded path of a source file, as the history knows it.
let keyOf (setup: Scratch) (name: string) : string =
    ScanRules.toMirrorPath (sourceFile setup name) |> ValueOption.get |> MirrorPath.value

/// Runs the backup through the command, which must succeed.
let backupOnce (setup: Scratch) : unit =
    let result = invoke [| "backup"; "docs"; "--config"; setup.Config |]
    Assert.Equal(0, result.ExitCode)

let private withProfile (setup: Scratch) (parts: string list) : string array =
    Array.ofList (parts @ [ "--profile"; "docs"; "--config"; setup.Config ])

/// A history command for the profile, as the user would type it.
let runEnv (customize: CliEnv -> CliEnv) (setup: Scratch) (parts: string list) : Invocation =
    invokeEnv customize CancellationToken.None (withProfile setup parts)

let run (setup: Scratch) (parts: string list) : Invocation = runEnv id setup parts

let liveCapabilities: Capabilities =
    { IsTerminal = true
      SupportsColor = true
      NoColorRequested = false
      InputIsInteractive = true }

/// An interactive session: prompts are answered by the functions, and the console is one that draws.
let interactive (prompts: Prompts) (env: CliEnv) : CliEnv =
    { env with
        Capabilities = liveCapabilities
        Prompts = prompts }

let private databaseOf (setup: Scratch) : string = Path.Combine(setup.Target, ".vara", "profile.db")

/// Every row of the manifest, read the way the commands read it.
let rowsOf (setup: Scratch) : FileVersion list =
    let handle = SqliteManifest.openReadOnly (databaseOf setup)

    try
        SqliteManifest.allVersions handle |> List.ofSeq
    finally
        SqliteManifest.close handle

let snapshotsOf (setup: Scratch) : SnapshotInfo list =
    let handle = SqliteManifest.openReadOnly (databaseOf setup)

    try
        SqliteManifest.snapshots handle |> List.sortBy _.SnapshotId
    finally
        SqliteManifest.close handle

/// A time between the first two snapshots, as text a date option accepts.
let betweenFirstTwo (setup: Scratch) : string =
    match snapshotsOf setup with
    | first :: second :: _ -> (first.StartedAt + (second.StartedAt - first.StartedAt) / 2.0).ToString "o"
    | _ -> failwith "expected two snapshots"

/// The version ids recorded for a source file, oldest first.
let versionIds (setup: Scratch) (name: string) : int64 list =
    rowsOf setup
    |> List.filter (fun version -> String.Equals(MirrorPath.value version.Row.RowPath, keyOf setup name, StringComparison.OrdinalIgnoreCase))
    |> List.map _.VersionId
    |> List.sort

let private pause () : unit = Thread.Sleep 30

/// Writes a source file, taking the time so that two snapshots never share a start time.
let changeFile (setup: Scratch) (name: string) (text: string) : unit =
    pause ()
    let path = sourceFile setup name
    Directory.CreateDirectory(Path.GetDirectoryName path |> nonNull) |> ignore
    File.WriteAllText(path, text)

let createJunction (junction: string) (target: string) : unit =
    let info = ProcessStartInfo("cmd.exe", $"/c mklink /J \"{junction}\" \"{target}\"")
    info.RedirectStandardOutput <- true
    info.RedirectStandardError <- true
    info.UseShellExecute <- false
    use proc = Process.Start info |> nonNull
    proc.WaitForExit()
    Assert.Equal(0, proc.ExitCode)

/// Marks a snapshot with the status, as the manifest of a run that ended that way would hold it.
let setStatus (setup: Scratch) (snapshotId: int64) (status: string) : unit =
    use connection = new SqliteConnection $"Data Source={databaseOf setup};Pooling=False"
    connection.Open()
    use command = connection.CreateCommand()
    command.CommandText <- "UPDATE snapshots SET status = $status WHERE id = $id"
    command.Parameters.AddWithValue("$status", status) |> ignore
    command.Parameters.AddWithValue("$id", snapshotId) |> ignore
    command.ExecuteNonQuery() |> ignore

/// A manifest that exists must not be changed by a reader.
let private manifestFingerprint (setup: Scratch) : string * DateTime =
    SqliteConnection.ClearAllPools()
    let path = databaseOf setup
    Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes path)), File.GetLastWriteTimeUtc path

// ---- snapshot-history ---------------------------------------------------------------------------------------------

module ``List snapshots`` =
    [<Fact>]
    let ``Listing snapshot history with positional profile`` () =
        scratch (fun setup ->
            write setup "a.txt" "alpha"
            backupOnce setup
            let result = invoke [| "snapshots"; "docs"; "--config"; setup.Config |]
            Assert.Equal(0, result.ExitCode)
            Assert.Contains("Complete", result.Output)
            Assert.Contains("Started", result.Output)
            Assert.Contains("Transferred", result.Output)
            Assert.Contains("5 B", result.Output))

    [<Fact>]
    let ``Listing snapshot history from target root without arguments`` () =
        scratch (fun setup ->
            write setup "a.txt" "alpha"
            backupOnce setup

            let result =
                invokeEnv (fun env -> { env with CurrentDirectory = setup.Target }) CancellationToken.None [| "snapshots"; "--config"; setup.Config |]

            Assert.Equal(0, result.ExitCode)
            Assert.Contains("Complete", result.Output))

    [<Fact>]
    let ``Listing snapshots without arguments outside target root`` () =
        scratch (fun setup ->
            write setup "a.txt" "alpha"
            backupOnce setup

            let result =
                invokeEnv (fun env -> { env with CurrentDirectory = setup.Root }) CancellationToken.None [| "snapshots"; "--config"; setup.Config |]

            Assert.Equal(1, result.ExitCode)
            Assert.Contains("profile name", result.Errors)
            Assert.DoesNotContain("Complete", result.Output))

    [<Fact>]
    let ``Listing snapshots for a profile that has never completed a backup run`` () =
        scratch (fun setup ->
            let result = invoke [| "snapshots"; "docs"; "--config"; setup.Config |]
            Assert.Equal(0, result.ExitCode)
            Assert.Contains("No snapshots recorded", result.Output)
            // A read-only request creates no storage.
            Assert.False(Directory.Exists setup.Target))

    [<Fact>]
    let ``Listing snapshot history`` () =
        scratch (fun setup ->
            write setup "a.txt" "alpha"
            backupOnce setup
            changeFile setup "a.txt" "alpha, changed"
            backupOnce setup
            let result = invoke [| "snapshots"; "docs"; "--config"; setup.Config |]
            // Two rows, newest first.
            let firstTwo = result.Output.Split '\n' |> Array.filter (fun line -> line.Contains "Complete")
            Assert.Equal(2, firstTwo.Length)
            Assert.StartsWith("│ 2", firstTwo[0].Replace("|", "│")))

    [<Fact>]
    let ``No snapshots recorded`` () =
        scratch (fun setup ->
            let result = invoke [| "snapshots"; "docs"; "--config"; setup.Config |]
            Assert.Contains("No snapshots recorded yet.", result.Output))

    [<Fact>]
    let ``Snapshot status reflects outcome severity`` () =
        scratch (fun setup ->
            write setup "a.txt" "alpha"
            backupOnce setup
            backupOnce setup
            setStatus setup 2L "Failed"
            let console = (new TestConsole()).EmitAnsiSequences()
            // The command's live output is coloured per status: the failed row carries the error colour's sequence.
            let live (env: CliEnv) = { env with Console = console; Capabilities = liveCapabilities }
            let result = invokeEnv live CancellationToken.None [| "snapshots"; "docs"; "--config"; setup.Config |]
            let text = console.Output
            Assert.True(text.Contains(char 27))
            Assert.Contains("Failed", text)
            Assert.Equal(0, result.ExitCode))

module ``File version history`` =
    [<Fact>]
    let ``Viewing a file's history`` () =
        scratch (fun setup ->
            write setup "a.txt" "alpha"
            backupOnce setup
            changeFile setup "a.txt" "alpha, changed"
            backupOnce setup
            let result = run setup [ "history"; sourceFile setup "a.txt" ]
            Assert.Equal(0, result.ExitCode)
            Assert.Contains("Added", result.Output)
            Assert.Contains("Changed", result.Output)
            Assert.True(result.Output.IndexOf "Changed" < result.Output.IndexOf "Added", "most recent first"))

    [<Fact>]
    let ``Change kind is visually distinguished`` () =
        scratch (fun setup ->
            write setup "a.txt" "alpha"
            backupOnce setup
            changeFile setup "a.txt" "alpha, changed"
            backupOnce setup
            let console = (new TestConsole()).EmitAnsiSequences()
            let live (env: CliEnv) = { env with Console = console; Capabilities = liveCapabilities }
            runEnv live setup [ "history"; sourceFile setup "a.txt" ] |> ignore
            let lines = console.Output.Split '\n'
            let added = lines |> Array.find (fun line -> line.Contains "Added")
            let changed = lines |> Array.find (fun line -> line.Contains "Changed")
            // Each kind has its own colour sequence before its name.
            let prefixOf (line: string) (word: string) : string = line.Substring(0, line.IndexOf word)
            Assert.NotEqual<string>(prefixOf added "Added", prefixOf changed "Changed"))

    [<Fact>]
    let ``No version history remains for a previously tracked path`` () =
        scratch (fun setup ->
            write setup "a.txt" "alpha"
            backupOnce setup
            let result = run setup [ "history"; sourceFile setup "gone-for-good.txt" ]
            Assert.Equal(1, result.ExitCode)
            Assert.Contains("No history exists for path", result.Errors)
            Assert.DoesNotContain("Recorded", result.Output))

module ``Unknown path or version reported clearly`` =
    [<Fact>]
    let ``Path never backed up`` () =
        scratch (fun setup ->
            write setup "a.txt" "alpha"
            backupOnce setup
            let result = run setup [ "history"; "never\\backed\\up.txt" ]
            Assert.Equal(1, result.ExitCode)
            Assert.Contains("No history exists for path 'never\\backed\\up.txt'.", result.Errors.Replace("\n", "")))

module ``Flexible path input for history, restore, show, and diff`` =
    let private historyOf (env: CliEnv -> CliEnv) (setup: Scratch) (input: string) : Invocation =
        runEnv env setup [ "history"; input ]

    let private found (result: Invocation) : bool = result.ExitCode = 0 && result.Output.Contains "Added"

    [<Fact>]
    let ``Path given in its mirror-relative form`` () =
        scratch (fun setup ->
            write setup "a.txt" "alpha"
            backupOnce setup
            Assert.True(found (historyOf id setup (keyOf setup "a.txt"))))

    [<Fact>]
    let ``Path relative to a working directory inside the mirror`` () =
        scratch (fun setup ->
            write setup "dir\\a.txt" "alpha"
            backupOnce setup
            let cwd = Path.GetDirectoryName(Path.Combine(setup.Target, keyOf setup "dir\\a.txt")) |> nonNull
            Assert.True(found (historyOf (fun env -> { env with CurrentDirectory = cwd }) setup "a.txt")))

    [<Fact>]
    let ``Path relative to a working directory inside the mirror, no cwd-relative match`` () =
        scratch (fun setup ->
            write setup "dir\\a.txt" "alpha"
            backupOnce setup
            // Inside the mirror, but the name is not recorded there: the input is then tried as an absolute source path.
            let cwd = Path.Combine(setup.Target, ".vara")
            Assert.True(found (historyOf (fun env -> { env with CurrentDirectory = cwd }) setup (sourceFile setup "dir\\a.txt"))))

    [<Fact>]
    let ``Path relative to a working directory inside a source`` () =
        scratch (fun setup ->
            write setup "dir\\a.txt" "alpha"
            backupOnce setup
            let cwd = Path.Combine(setup.Source, "dir")
            Assert.True(found (historyOf (fun env -> { env with CurrentDirectory = cwd }) setup "a.txt")))

    [<Fact>]
    let ``Absolute source path given directly`` () =
        scratch (fun setup ->
            write setup "a.txt" "alpha"
            backupOnce setup
            Assert.True(found (historyOf id setup (sourceFile setup "a.txt"))))

    [<Fact>]
    let ``No interpretation matches recorded history`` () =
        scratch (fun setup ->
            write setup "a.txt" "alpha"
            backupOnce setup
            let result = historyOf id setup "nothing-like-this.txt"
            Assert.Equal(1, result.ExitCode)
            Assert.Contains("No history exists", result.Errors))

    [<Fact>]
    let ``Browsing or recursively restoring the current directory from within a source`` () =
        scratch (fun setup ->
            write setup "dir\\a.txt" "alpha"
            backupOnce setup
            let result = runEnv (fun env -> { env with CurrentDirectory = Path.Combine(setup.Source, "dir") }) setup [ "browse"; "." ]
            Assert.Equal(0, result.ExitCode)
            Assert.Contains("a.txt", result.Output)
            Assert.DoesNotContain("falls back", result.Output))

    [<Fact>]
    let ``Current directory outside the mirror has no recorded history`` () =
        scratch (fun setup ->
            write setup "dir\\a.txt" "alpha"
            backupOnce setup
            let result = run setup [ "browse"; "." ]
            Assert.Equal(0, result.ExitCode)
            Assert.Contains("no recorded history", result.Output))

    [<Fact>]
    let ``Non-root directory argument from within a source is unaffected`` () =
        scratch (fun setup ->
            write setup "dir\\a.txt" "alpha"
            backupOnce setup
            let directory = Path.GetDirectoryName(keyOf setup "dir\\a.txt") |> nonNull
            let result = runEnv (fun env -> { env with CurrentDirectory = setup.Source }) setup [ "browse"; directory ]
            Assert.Equal(0, result.ExitCode)
            Assert.Contains("a.txt", result.Output)
            Assert.DoesNotContain("no recorded history", result.Output))

