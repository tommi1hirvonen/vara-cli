/// Tests of writing the configuration file: the YAML text and the durable write. A test is named after a scenario of
/// openspec/specs/profile-management/spec.md and sits in a module named after its requirement; the tests whose names are
/// not a scenario's check the writer more closely.
module Vara.Engine.Tests.ProfileWriterTests

open System
open System.Diagnostics
open System.IO
open Xunit
open FsCheck
open FsCheck.FSharp
open FsCheck.Xunit
open Vara.Domain
open Vara.Domain.Tests
open Vara.Engine
open Vara.Engine.Tests

let private profileOf = ProfileEditorTests.profileOf

let private docs = profileOf "docs" @"C:\backup\docs" [ @"C:\data\docs" ]
let private photos = profileOf "photos" @"C:\backup\photos" [ @"C:\data\photos"; @"C:\data\more" ]

/// A profile with every optional field set, and one with none.
let everything: Profile =
    let node =
        ConfigNode.Mapping
            [ "name", ConfigNode.Scalar "all"
              "target", ConfigNode.Scalar @"C:\backup\all"
              "sources",
              ConfigNode.Sequence
                  [ ConfigNode.Mapping
                        [ "path", ConfigNode.Scalar @"C:\data\one"
                          "recursive", ConfigNode.Scalar "false"
                          "exclude", ConfigNode.Sequence [ ConfigNode.Scalar "tmp"; ConfigNode.Scalar "a b" ]
                          "include_globs", ConfigNode.Sequence [ ConfigNode.Scalar "**/*.md" ]
                          "exclude_globs", ConfigNode.Sequence [ ConfigNode.Scalar "*.bak" ] ]
                    ConfigNode.Mapping [ "path", ConfigNode.Scalar @"C:\data\two" ] ]
              "retention",
              ConfigNode.Mapping
                  [ "keep_daily", ConfigNode.Scalar "7"
                    "keep_weekly", ConfigNode.Scalar "4"
                    "keep_monthly", ConfigNode.Scalar "12"
                    "keep_yearly", ConfigNode.Scalar "3" ]
              "concurrency", ConfigNode.Mapping [ "scan_concurrency", ConfigNode.Scalar "4"; "transfer_concurrency", ConfigNode.Scalar "8" ] ]

    match ProfileValidation.validateProfiles [ node ] with
    | Ok [ profile ] -> profile
    | _ -> failwith "fixture"

let private loaded (path: string) : Profile list =
    match ConfigFile.load path with
    | Ok profiles -> profiles
    | Error error -> failwith (ConfigError.render error)

let private saveOk (path: string) (profiles: Profile list) : unit =
    match ConfigFile.save path profiles with
    | Ok() -> ()
    | Error error -> failwith (ConfigWriteError.render error)

let private configIn (directory: string) : string = Path.Combine(directory, "profiles.yml")

/// What is in a directory besides the configuration file: a temporary file that was left behind would show.
let private strays (directory: string) : string list =
    Directory.GetFiles directory |> Array.filter (fun file -> file <> configIn directory) |> List.ofArray

// ---- The text ----

module ``YAML text`` =
    let private roundTrip (profiles: Profile list) : Result<Profile list, ConfigError> =
        ProfileNodes.ofProfiles profiles
        |> YamlConfig.render
        |> YamlConfig.parse "profiles.yml"
        |> Result.bind ProfileValidation.validateProfiles

    [<Fact>]
    let ``the text parses back to the same tree`` () =
        let nodes = ProfileNodes.ofProfiles [ docs; photos; everything ]
        Assert.Equal(Ok nodes, YamlConfig.parse "profiles.yml" (YamlConfig.render nodes))

    [<Fact>]
    let ``a list with every optional field set, and one with none, round-trip`` () =
        Assert.Equal(Ok [ everything ], roundTrip [ everything ])
        Assert.Equal(Ok [ docs ], roundTrip [ docs ])
        Assert.Equal(Ok [], roundTrip [])

    [<Fact>]
    let ``an optional field that is not set is not written`` () =
        let text = YamlConfig.render (ProfileNodes.ofProfiles [ docs ])
        Assert.DoesNotContain("retention", text)
        Assert.DoesNotContain("concurrency", text)
        Assert.DoesNotContain("exclude", text)

    [<Property>]
    let ``generated profiles round-trip through the text and the loader`` () =
        Prop.forAll (ProfileEditorPropertyTests.Generators.arbitrary ProfileEditorPropertyTests.Generators.profiles) (fun profiles ->
            roundTrip profiles = Ok profiles)

// ---- Regeneration ----

module ``Saving regenerates the whole configuration file without preserving prior formatting`` =
    [<Fact>]
    let ``Saving a change discards unrelated hand-authored comments`` () =
        TempDir.using (fun directory ->
            let path = configIn directory

            File.WriteAllText(
                path,
                "# explains why docs is backed up\nprofiles:\n  - name: docs # the main one\n    target: C:\\backup\\docs\n    sources:\n      - path: C:\\data\\docs\n"
            )

            let existing = loaded path
            saveOk path (existing @ [ photos ])

            let text = File.ReadAllText path
            Assert.DoesNotContain("explains", text)
            Assert.DoesNotContain("the main one", text)
            // The set of profiles and their values are what is kept.
            Assert.Equal<Profile list>([ docs; photos ], loaded path))

    [<Fact>]
    let ``Interrupted write does not corrupt the configuration file`` () =
        TempDir.using (fun directory ->
            let path = configIn directory
            saveOk path [ docs ]
            let before = File.ReadAllText path

            let content = YamlConfig.render (ProfileNodes.ofProfiles [ docs; photos ])

            // The write stops part-way: the temporary file holds half the text.
            let partial =
                { ConfigFile.DurableFiles.real with
                    WriteText =
                        fun temporary text ->
                            File.WriteAllText(temporary, text.Substring(0, text.Length / 2))
                            raise (IOException "The process was interrupted.") }

            Assert.True(Result.isError (ConfigFile.writeDurablyWith partial path content))
            Assert.Equal(before, File.ReadAllText path)
            Assert.Empty(strays directory)

            // An exception of another kind leaves the same way, with the file intact and no file left behind.
            let unexpected = { ConfigFile.DurableFiles.real with Replace = fun _ _ -> raise (InvalidOperationException "unexpected") }
            let thrown = Assert.Throws<InvalidOperationException>(fun () -> ConfigFile.writeDurablyWith unexpected path content |> ignore)
            Assert.False(String.IsNullOrEmpty thrown.StackTrace)
            Assert.Equal(before, File.ReadAllText path)
            Assert.Empty(strays directory)

            // Once the write has completed and been moved into place, the file is the new state.
            Assert.Equal(Ok(), ConfigFile.writeDurably path content)
            Assert.Equal(content, File.ReadAllText path)
            Assert.Empty(strays directory))

// ---- Durability ----

module ``A configuration write is durable before it replaces the previous file`` =
    /// The real operations, each recorded as its name and the files it was given.
    let private recording (calls: ResizeArray<string * string list>) : ConfigFile.DurableFiles =
        let real = ConfigFile.DurableFiles.real
        let record name (paths: string list) = calls.Add(name, paths |> List.map Path.GetFileName)

        { CreateDirectory =
            fun directory ->
                record "CreateDirectory" [ directory ]
                real.CreateDirectory directory
          WriteText =
            fun path text ->
                record "WriteText" [ path ]
                real.WriteText path text
          Commit =
            fun path ->
                record "Commit" [ path ]
                real.Commit path
          Exists = real.Exists
          Replace =
            fun temporary target ->
                record "Replace" [ temporary; target ]
                real.Replace temporary target
          Move =
            fun temporary target ->
                record "Move" [ temporary; target ]
                real.Move temporary target
          Delete =
            fun path ->
                record "Delete" [ path ]
                real.Delete path }

    [<Fact>]
    let ``Abrupt loss of the machine during a write`` () =
        TempDir.using (fun directory ->
            let path = configIn directory
            saveOk path [ docs ]
            let calls = ResizeArray<string * string list>()

            Assert.Equal(Ok(), ConfigFile.writeDurablyWith (recording calls) path "profiles: []\n")

            let order = calls |> Seq.map fst |> List.ofSeq
            // The temporary file is written and committed to disk before it replaces the previous file, which no step
            // before the replacement touches.
            Assert.Equal<string list>([ "CreateDirectory"; "WriteText"; "Commit"; "Replace" ], order)
            let temporaryOf name = calls |> Seq.find (fun (call, _) -> call = name) |> snd |> List.head
            Assert.Equal(temporaryOf "WriteText", temporaryOf "Commit")
            Assert.Equal(temporaryOf "Commit", temporaryOf "Replace")
            Assert.NotEqual<string>("profiles.yml", temporaryOf "WriteText")

            // The first write, with nothing to replace, moves the committed file into place instead.
            let fresh = Path.Combine(directory, "fresh", "profiles.yml")
            let freshCalls = ResizeArray<string * string list>()
            Assert.Equal(Ok(), ConfigFile.writeDurablyWith (recording freshCalls) fresh "profiles: []\n")
            Assert.Equal<string list>([ "CreateDirectory"; "WriteText"; "Commit"; "Move" ], freshCalls |> Seq.map fst |> List.ofSeq))

    [<Fact>]
    let ``A completed save is not lost to a subsequent crash`` () =
        TempDir.using (fun directory ->
            let path = configIn directory
            let calls = ResizeArray<string * string list>()
            let text = YamlConfig.render (ProfileNodes.ofProfiles [ docs; photos ])

            // The write reports success only after the commit and the replacement, and then the file holds the list.
            Assert.Equal(Ok(), ConfigFile.writeDurablyWith (recording calls) path text)
            Assert.Equal<string list>([ "CreateDirectory"; "WriteText"; "Commit"; "Move" ], calls |> Seq.map fst |> List.ofSeq)
            Assert.Equal<Profile list>([ docs; photos ], loaded path))

// ---- Failure ----

module ``A failed configuration write is recoverable within the session`` =
    let private icacls (arguments: string) : int =
        let info = ProcessStartInfo("cmd.exe", $"/c icacls {arguments}")
        info.RedirectStandardOutput <- true
        info.RedirectStandardError <- true
        info.UseShellExecute <- false
        use proc = Process.Start info |> nonNull
        proc.WaitForExit()
        proc.ExitCode

    let private denyAccess (path: string) : unit =
        Assert.Equal(0, icacls $"\"{path}\" /deny \"%%USERNAME%%:(R,W)\"")

    let private restoreAccess (path: string) : unit =
        icacls $"\"{path}\" /remove:d \"%%USERNAME%%\"" |> ignore

    let private expectFailure (path: string) (outcome: Result<unit, ConfigWriteError>) : ConfigWriteError =
        match outcome with
        | Error error ->
            // It names the file and the reason, and it is a message, not a diagnostic dump.
            Assert.Equal(path, error.ConfigPath)
            Assert.False(String.IsNullOrWhiteSpace error.Reason)
            let message = ConfigWriteError.render error
            Assert.Contains(path, message)
            Assert.DoesNotContain("   at ", message)
            Assert.DoesNotContain("Exception", message)
            error
        | Ok() -> failwith "expected the write to fail"

    [<Fact>]
    let ``Save fails because the configuration file cannot be written`` () =
        TempDir.using (fun directory ->
            let path = configIn directory
            saveOk path [ docs ]
            let before = File.ReadAllText path

            // Read-only.
            File.SetAttributes(path, FileAttributes.ReadOnly)
            expectFailure path (ConfigFile.save path [ docs; photos ]) |> ignore
            Assert.Equal(before, File.ReadAllText path)
            Assert.Empty(strays directory)

            // Once the cause is gone, the same save succeeds.
            File.SetAttributes(path, FileAttributes.Normal)
            saveOk path [ docs; photos ]
            Assert.Equal<Profile list>([ docs; photos ], loaded path)

            // A directory that cannot be written to.
            let locked = Path.Combine(directory, "locked")
            Directory.CreateDirectory locked |> ignore
            let lockedPath = configIn locked
            saveOk lockedPath [ docs ]
            let lockedBefore = File.ReadAllText lockedPath
            denyAccess locked

            try
                expectFailure lockedPath (ConfigFile.save lockedPath [ docs; photos ]) |> ignore
            finally
                restoreAccess locked

            Assert.Equal(lockedBefore, File.ReadAllText lockedPath)
            Assert.Empty(strays locked))

    [<Fact>]
    let ``the first write creates the file and its directory`` () =
        TempDir.using (fun directory ->
            let path = Path.Combine(directory, "home", ".vara", "profiles.yml")
            saveOk path [ docs ]
            Assert.Equal<Profile list>([ docs ], loaded path)
            Assert.Empty(strays (Path.GetDirectoryName path |> nonNull)))

    [<Fact>]
    let ``a directory that cannot be created is a failure that names the file`` () =
        TempDir.using (fun directory ->
            // The parent of the directory is a file.
            let blocker = Path.Combine(directory, "blocker")
            File.WriteAllText(blocker, "not a directory")
            let path = Path.Combine(blocker, "profiles.yml")
            expectFailure path (ConfigFile.save path [ docs ]) |> ignore
            Assert.Equal("not a directory", File.ReadAllText blocker))
