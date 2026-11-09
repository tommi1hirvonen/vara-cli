/// Tests of `vara show` and `vara diff`, through the command: what is streamed or diffed, and every refusal. Each
/// module is named after a requirement of openspec/specs/snapshot-history and each test after one of its scenarios.
module Vara.Cli.Tests.ShowDiffCommandTests

open System
open System.IO
open System.Text
open Xunit
open Vara.Cli
open Vara.Cli.Tests.BackupCommandTests
open Vara.Cli.Tests.HistoryCommandTests
open Vara.Domain
open Vara.Engine

/// Runs `show` with standard output captured as bytes; `terminal` says whether that output is an interactive terminal.
let private showOutput (terminal: bool) (setup: Scratch) (parts: string list) : Invocation * byte[] =
    let captured = new MemoryStream()

    let customize (env: CliEnv) : CliEnv =
        { env with
            Capabilities = { env.Capabilities with IsTerminal = terminal }
            OpenStandardOutput = fun () -> new NonClosingStream(captured) :> Stream }

    let result = runEnv customize setup ([ "show" ] @ parts)
    result, captured.ToArray()

let private text (bytes: byte[]) : string = Encoding.UTF8.GetString bytes

let private writeBytes (setup: Scratch) (name: string) (bytes: byte[]) : unit =
    let path = sourceFile setup name
    Directory.CreateDirectory(Path.GetDirectoryName path |> nonNull) |> ignore
    File.WriteAllBytes(path, bytes)

module ``Show a version's content directly`` =
    [<Fact>]
    let ``Showing a text file's historical content`` () =
        scratch (fun setup ->
            write setup "a.txt" "first content"
            backupOnce setup
            changeFile setup "a.txt" "second content, longer"
            backupOnce setup
            let first = (versionIds setup "a.txt").Head
            let before = Directory.GetFiles(setup.Target, "*", SearchOption.AllDirectories).Length
            let result, bytes = showOutput false setup [ sourceFile setup "a.txt"; "--version"; string first ]
            Assert.Equal(0, result.ExitCode)
            Assert.Equal("first content", text bytes)
            // Nothing was written anywhere.
            Assert.Equal(before, Directory.GetFiles(setup.Target, "*", SearchOption.AllDirectories).Length))

    [<Fact>]
    let ``Showing an unknown path or version`` () =
        scratch (fun setup ->
            write setup "a.txt" "alpha"
            backupOnce setup
            let unknownPath, none = showOutput false setup [ "never.txt"; "--version"; "1" ]
            Assert.Equal(1, unknownPath.ExitCode)
            Assert.Contains("No history exists", unknownPath.Errors)
            Assert.Empty none
            let unknownVersion, _ = showOutput false setup [ sourceFile setup "a.txt"; "--version"; "999" ]
            Assert.Equal(1, unknownVersion.ExitCode)
            Assert.Contains("No version with id 999", unknownVersion.Errors.Replace("\n", "")))

    [<Fact>]
    let ``Refusing binary content to an interactive terminal`` () =
        scratch (fun setup ->
            writeBytes setup "b.bin" [| 1uy; 2uy; 0uy; 3uy |]
            backupOnce setup
            let result, bytes = showOutput true setup [ sourceFile setup "b.bin"; "--version"; string (versionIds setup "b.bin").Head ]
            Assert.Equal(1, result.ExitCode)
            Assert.Contains("appears to be binary", result.Errors.Replace("\n", ""))
            Assert.Contains("b.bin", result.Errors.Replace("\n", ""))
            Assert.Empty bytes)

    [<Fact>]
    let ``Binary content streamed when output is redirected`` () =
        scratch (fun setup ->
            writeBytes setup "b.bin" [| 1uy; 2uy; 0uy; 3uy |]
            backupOnce setup
            let result, bytes = showOutput false setup [ sourceFile setup "b.bin"; "--version"; string (versionIds setup "b.bin").Head ]
            Assert.Equal(0, result.ExitCode)
            Assert.Equal<byte[]>([| 1uy; 2uy; 0uy; 3uy |], bytes))

    [<Fact>]
    let ``Binary content forced to an interactive terminal`` () =
        scratch (fun setup ->
            writeBytes setup "b.bin" [| 1uy; 2uy; 0uy; 3uy |]
            backupOnce setup
            let result, bytes = showOutput true setup [ sourceFile setup "b.bin"; "--version"; string (versionIds setup "b.bin").Head; "--force-binary" ]
            Assert.Equal(0, result.ExitCode)
            Assert.Equal<byte[]>([| 1uy; 2uy; 0uy; 3uy |], bytes))

    [<Fact>]
    let ``Showing a linked (symlink/junction) version`` () =
        scratch (fun setup ->
            Directory.CreateDirectory(Path.Combine(setup.Root, "elsewhere")) |> ignore
            createJunction (sourceFile setup "link") (Path.Combine(setup.Root, "elsewhere"))
            backupOnce setup
            let result, bytes = showOutput false setup [ sourceFile setup "link"; "--version"; string (versionIds setup "link").Head ]
            Assert.Equal(1, result.ExitCode)
            Assert.Contains("symlink/junction with no stored content", result.Errors.Replace("\n", ""))
            Assert.Empty bytes)

    [<Fact>]
    let ``the version is named by a date or an id, not both and not neither`` () =
        scratch (fun setup ->
            write setup "a.txt" "alpha"
            backupOnce setup
            let both, _ = showOutput false setup [ sourceFile setup "a.txt"; "--version"; "1"; "--at"; "2020-01-01" ]
            Assert.Equal(1, both.ExitCode)
            Assert.Contains("mutually exclusive", both.Errors)
            let neither, _ = showOutput false setup [ sourceFile setup "a.txt" ]
            Assert.Equal(1, neither.ExitCode)
            Assert.Contains("specify either --at <date> or --version <id>", neither.Errors))

module ``Diff two versions of a file`` =
    let private diff (setup: Scratch) (parts: string list) : Invocation = run setup ([ "diff" ] @ parts)

    let private twoVersions (setup: Scratch) (first: string) (second: string) : string list =
        write setup "a.txt" first
        backupOnce setup
        changeFile setup "a.txt" second
        backupOnce setup
        let ids = versionIds setup "a.txt"
        [ sourceFile setup "a.txt"; "--left-version"; string ids[0]; "--right-version"; string ids[1] ]

    [<Fact>]
    let ``Diffing two recorded versions`` () =
        scratch (fun setup ->
            let parts = twoVersions setup "one\ntwo\nthree" "one\n2\nthree"
            let result = diff setup parts
            Assert.Equal(0, result.ExitCode)
            Assert.Contains("- two", result.Output)
            Assert.Contains("+ 2", result.Output)
            Assert.Contains("  one", result.Output))

    [<Fact>]
    let ``Diffing an unknown path or version`` () =
        scratch (fun setup ->
            let parts = twoVersions setup "a" "b"
            let unknownPath = diff setup [ "never.txt"; "--left-version"; "1"; "--right-version"; "2" ]
            Assert.Equal(1, unknownPath.ExitCode)
            Assert.Contains("No history exists", unknownPath.Errors)
            let unknownVersion = diff setup (parts |> List.take 1 |> fun path -> path @ [ "--left-version"; "999"; "--right-version"; "1" ])
            Assert.Equal(1, unknownVersion.ExitCode)
            Assert.Contains("No version with id 999", unknownVersion.Errors.Replace("\n", "")))

    let private big = String('x', 10 * 1024 * 1024 + 1)

    [<Fact>]
    let ``Refusing an oversized version`` () =
        scratch (fun setup ->
            let parts = twoVersions setup "small" big
            let result = diff setup parts
            Assert.Equal(1, result.ExitCode)
            let errors = result.Errors.Replace("\n", "")
            Assert.Contains("right side is 10,485,761 bytes", errors)
            Assert.DoesNotContain("left side", errors)
            Assert.DoesNotContain("-", result.Output))

    [<Fact>]
    let ``Refusing binary content`` () =
        scratch (fun setup ->
            writeBytes setup "a.txt" [| 65uy; 0uy; 66uy |]
            backupOnce setup
            changeFile setup "a.txt" "plain text"
            backupOnce setup
            let ids = versionIds setup "a.txt"
            let result = diff setup [ sourceFile setup "a.txt"; "--left-version"; string ids[0]; "--right-version"; string ids[1] ]
            Assert.Equal(1, result.ExitCode)
            let errors = result.Errors.Replace("\n", "")
            Assert.Contains("left side appears to be binary content", errors)
            Assert.DoesNotContain("right side", errors))

    [<Fact>]
    let ``Both versions exceed the size limit`` () =
        scratch (fun setup ->
            let parts = twoVersions setup big (big + "y")
            let errors = (diff setup parts).Errors.Replace("\n", "")
            Assert.Contains("left side is 10,485,761 bytes and right side is 10,485,762 bytes", errors))

    [<Fact>]
    let ``One version too large, the other binary`` () =
        scratch (fun setup ->
            writeBytes setup "a.txt" [| 65uy; 0uy; 66uy |]
            backupOnce setup
            changeFile setup "a.txt" big
            backupOnce setup
            let ids = versionIds setup "a.txt"
            let result = diff setup [ sourceFile setup "a.txt"; "--left-version"; string ids[0]; "--right-version"; string ids[1] ]
            Assert.Equal(1, result.ExitCode)
            let errors = result.Errors.Replace("\n", "")
            // Each side's own problem is named.
            Assert.Contains("right side is 10,485,761 bytes", errors)
            Assert.Contains("exceeding", errors)
            Assert.Contains("left side appears to be binary content", errors))

    [<Fact>]
    let ``Diffing a linked (symlink/junction) version`` () =
        scratch (fun setup ->
            Directory.CreateDirectory(Path.Combine(setup.Root, "elsewhere")) |> ignore
            createJunction (sourceFile setup "link") (Path.Combine(setup.Root, "elsewhere"))
            backupOnce setup
            let id = (versionIds setup "link").Head
            let result = diff setup [ sourceFile setup "link"; "--left-version"; string id; "--right-version"; string id ]
            Assert.Equal(1, result.ExitCode)
            Assert.Contains("left side and right side is a symlink/junction with no stored content", result.Errors.Replace("\n", "")))

    [<Fact>]
    let ``One side linked, the other side's content already opened`` () =
        scratch (fun setup ->
            write setup "a.txt" "regular content"
            backupOnce setup
            // The path then becomes a link: its history holds a regular version and a linked one.
            File.Delete(sourceFile setup "a.txt")
            Directory.CreateDirectory(Path.Combine(setup.Root, "elsewhere")) |> ignore
            changeFile setup "unused.txt" "x"
            createJunction (sourceFile setup "a.txt") (Path.Combine(setup.Root, "elsewhere"))
            backupOnce setup
            let linked =
                rowsOf setup
                |> List.find (fun version -> version.Row.Kind = ChangeKind.Linked && MirrorPath.value version.Row.RowPath = keyOf setup "a.txt")

            let regular = (versionIds setup "a.txt").Head
            let result = diff setup [ sourceFile setup "a.txt"; "--left-version"; string regular; "--right-version"; string linked.VersionId ]
            Assert.Equal(1, result.ExitCode)
            Assert.Contains("right side is a symlink/junction", result.Errors.Replace("\n", ""))

            // The other side's content is not left open: its blob can be opened exclusively.
            let hash =
                match (rowsOf setup |> List.find (fun version -> version.VersionId = regular)).Row.Content with
                | RowContent.Regular content -> ContentHash.value content.Hash
                | RowContent.Linked _ -> failwith "regular row expected"

            let blob = Path.Combine(setup.Target, ".vara", "versions", hash.Substring(0, 2), hash)
            use exclusive = new FileStream(blob, FileMode.Open, FileAccess.Read, FileShare.None)
            Assert.True exclusive.CanRead)
