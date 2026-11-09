/// One test per owned scenario of openspec/specs/profile-config/spec.md. A test is named after its scenario
/// and sits in a module named after its requirement, so the spec-coverage check can find it.
module Vara.Domain.Tests.ProfileValidationTests

open Xunit
open Vara.Domain

module Node =
    let scalar text = ConfigNode.Scalar text
    let sequence items = ConfigNode.Sequence items
    let mapping fields = ConfigNode.Mapping fields
    let source path = mapping [ ("path", scalar path) ]

    let profile name target sources =
        mapping [ "name", scalar name; "target", scalar target; "sources", sequence sources ]

    let withFields extra node =
        match node with
        | ConfigNode.Mapping fields -> mapping (fields @ extra)
        | other -> other

let private validate entries = ProfileValidation.validateProfiles entries

let private expectProfile (outcome: Result<Profile list, ConfigError>) : Profile =
    match outcome with
    | Ok [ profile ] -> profile
    | Ok other -> Assert.Fail $"expected one profile, got {List.length other}"; failwith "unreachable"
    | Error error -> Assert.Fail(ConfigError.render error); failwith "unreachable"

let private expectInvalid (name: string) (reason: string) (outcome: Result<Profile list, ConfigError>) : unit =
    match outcome with
    | Error error -> Assert.Equal(ConfigError.InvalidProfile(name, reason), error)
    | Ok _ -> Assert.Fail "expected a validation error"

let private sourcePaths (profile: Profile) : string list =
    Profile.sources profile |> List.map (fun source -> AbsolutePath.value source.Path)

module ``Profile structure validation`` =
    [<Fact>]
    let ``Missing required field`` () =
        validate [ Node.mapping [ "name", Node.scalar "p"; "sources", Node.sequence [ Node.source @"C:\a" ] ] ]
        |> expectInvalid "p" "missing required field 'target'"

        validate [ Node.profile "p" @"C:\t" [] ]
        |> expectInvalid "p" "must define at least one source under 'sources'"

    [<Fact>]
    let ``Source without recursive flag`` () =
        let profile = validate [ Node.profile "p" @"C:\t" [ Node.source @"C:\a" ] ] |> expectProfile
        Assert.True (List.head (Profile.sources profile)).Recursive

    [<Fact>]
    let ``Exclude field given as a scalar instead of a list`` () =
        for field in [ "exclude"; "include_globs"; "exclude_globs" ] do
            let source = Node.mapping [ "path", Node.scalar @"C:\a"; field, Node.scalar "*.tmp" ]

            validate [ Node.profile "p" @"C:\t" [ source ] ]
            |> expectInvalid "p" $"source 'C:\\a' field '{field}' must be a list, but found a scalar"

    [<Fact>]
    let ``Exclude list contains a non-string entry`` () =
        let entry = Node.mapping [ ("k", Node.scalar "v") ]
        let source = Node.mapping [ "path", Node.scalar @"C:\a"; "exclude", Node.sequence [ entry ] ]

        validate [ Node.profile "p" @"C:\t" [ source ] ]
        |> expectInvalid "p" @"source 'C:\a' field 'exclude' contains an entry that is a mapping instead of a string"

        let nested = Node.mapping [ "path", Node.scalar @"C:\a"; "exclude_globs", Node.sequence [ Node.sequence [] ] ]

        validate [ Node.profile "p" @"C:\t" [ nested ] ]
        |> expectInvalid "p" @"source 'C:\a' field 'exclude_globs' contains an entry that is a nested list instead of a string"

    [<Fact>]
    let ``Well-formed exclude/glob lists accepted`` () =
        let strings items = Node.sequence (items |> List.map Node.scalar)

        let source =
            Node.mapping
                [ "path", Node.scalar @"C:\a"
                  "exclude", strings [ "x" ]
                  "include_globs", strings [ "*.cs"; "*.fs" ]
                  "exclude_globs", strings [] ]

        let accepted = validate [ Node.profile "p" @"C:\t" [ source ] ] |> expectProfile |> Profile.sources |> List.head
        Assert.Equal<string list>([ "x" ], accepted.Excludes)
        Assert.Equal<string list>([ "*.cs"; "*.fs" ], accepted.IncludeGlobs)
        Assert.Equal<string list>([], accepted.ExcludeGlobs)

module ``Source and target paths must be absolute`` =
    [<Fact>]
    let ``Relative target root rejected`` () =
        for target in [ @"backup\"; @"..\backup" ] do
            validate [ Node.profile "p" target [ Node.source @"C:\a" ] ]
            |> expectInvalid "p" $"Target root '{target}' must be a fully-qualified, absolute path."

    [<Fact>]
    let ``Relative source path rejected`` () =
        for source in [ "Documents"; @".\src" ] do
            validate [ Node.profile "p" @"C:\t" [ Node.source source ] ]
            |> expectInvalid "p" $"Source path '{source}' must be a fully-qualified, absolute path."

    [<Fact>]
    let ``Absolute target and source paths accepted`` () =
        let profile =
            validate [ Node.profile "p" @"C:\Backups\vara" [ Node.source @"C:\Users\me\Documents" ] ]
            |> expectProfile

        Assert.Equal(@"C:\Backups\vara", AbsolutePath.value (Profile.target profile))

    [<Fact>]
    let ``Forward-slash source path normalized to the native separator`` () =
        let profile = validate [ Node.profile "p" @"C:\t" [ Node.source "C:/Users/me/Docs" ] ] |> expectProfile
        Assert.Equal<string list>([ @"C:\Users\me\Docs" ], sourcePaths profile)

    [<Fact>]
    let ``Forward-slash target root normalized to the native separator`` () =
        let profile = validate [ Node.profile "p" "C:/Backups/vara" [ Node.source @"C:\a" ] ] |> expectProfile
        Assert.Equal(@"C:\Backups\vara", AbsolutePath.value (Profile.target profile))

    [<Fact>]
    let ``Equivalent path forms normalize to the same stored value`` () =
        let forms = [ "C:/Data/x"; @"C:\Data\x"; @"C:\\Data\\x"; @"C:\Data\.\x"; @"C:\Data\y\..\x" ]

        let stored =
            forms
            |> List.map (fun form ->
                validate [ Node.profile "p" @"C:\t" [ Node.source form ] ]
                |> expectProfile
                |> sourcePaths)

        Assert.Equal<string list list>(List.replicate forms.Length [ @"C:\Data\x" ], stored)

    [<Fact>]
    let ``Trailing separator is not stripped`` () =
        let profile = validate [ Node.profile "p" @"C:\Backups\vara\" [ Node.source @"C:\a\" ] ] |> expectProfile
        Assert.Equal(@"C:\Backups\vara\", AbsolutePath.value (Profile.target profile))
        Assert.Equal<string list>([ @"C:\a\" ], sourcePaths profile)

module ``Target root must not overlap a profile's own sources`` =
    let private targetOverlap target source =
        $"Target root '{target}' overlaps source path '{source}'. "
        + "A profile's target must not be the same as, contain, or be contained by any of its own sources."

    [<Fact>]
    let ``Target equals a source path`` () =
        validate [ Node.profile "p" @"C:\Data" [ Node.source @"C:\Data" ] ]
        |> expectInvalid "p" (targetOverlap @"C:\Data" @"C:\Data")

    [<Fact>]
    let ``Target is nested inside a source path`` () =
        validate [ Node.profile "p" @"C:\Users\me\backup" [ Node.source @"C:\Users\me\" ] ]
        |> expectInvalid "p" (targetOverlap @"C:\Users\me\backup" @"C:\Users\me\")

    [<Fact>]
    let ``A source path is nested inside the target root`` () =
        validate [ Node.profile "p" @"C:\Users\me\backup" [ Node.source @"C:\Users\me\backup\Documents" ] ]
        |> expectInvalid "p" (targetOverlap @"C:\Users\me\backup" @"C:\Users\me\backup\Documents")

    [<Fact>]
    let ``Case and trailing separator differences still detected as overlap`` () =
        validate [ Node.profile "p" @"c:\backup\" [ Node.source @"C:\Backup" ] ]
        |> expectInvalid "p" (targetOverlap @"c:\backup\" @"C:\Backup")

    [<Fact>]
    let ``Non-overlapping target and sources accepted`` () =
        validate [ Node.profile "p" @"C:\Backups" [ Node.source @"C:\Data"; Node.source @"C:\Other" ] ]
        |> expectProfile
        |> ignore

    [<Fact>]
    let ``Overlap with a different profile's source is not checked`` () =
        let accepted =
            validate
                [ Node.profile "one" @"C:\Backups\one" [ Node.source @"C:\Data" ]
                  Node.profile "two" @"C:\Data\two" [ Node.source @"C:\Elsewhere" ] ]

        Assert.True(Result.isOk accepted)

module ``A profile's own sources must not overlap each other`` =
    let private sourceOverlap first second =
        $"Source path '{first}' overlaps source path '{second}'. "
        + "A profile's sources must not be the same as, contain, or be contained by any of its own other sources."

    [<Fact>]
    let ``Two source paths are identical`` () =
        validate [ Node.profile "p" @"C:\t" [ Node.source @"C:\Data"; Node.source @"C:\Data" ] ]
        |> expectInvalid "p" (sourceOverlap @"C:\Data" @"C:\Data")

    [<Fact>]
    let ``One source path is nested inside another source path`` () =
        validate [ Node.profile "p" @"C:\t" [ Node.source @"C:\Users\me"; Node.source @"C:\Users\me\Documents" ] ]
        |> expectInvalid "p" (sourceOverlap @"C:\Users\me" @"C:\Users\me\Documents")

    [<Fact>]
    let ``Case and trailing separator differences still detected as overlap`` () =
        validate [ Node.profile "p" @"C:\t" [ Node.source @"C:\Data\"; Node.source @"c:\Data" ] ]
        |> expectInvalid "p" (sourceOverlap @"C:\Data\" @"c:\Data")

    [<Fact>]
    let ``Non-overlapping sources accepted`` () =
        validate [ Node.profile "p" @"C:\t" [ Node.source @"C:\Data"; Node.source @"C:\Database" ] ]
        |> expectProfile
        |> ignore

    [<Fact>]
    let ``Overlap with a different profile's source is not checked`` () =
        let accepted =
            validate
                [ Node.profile "one" @"C:\t1" [ Node.source @"C:\Data" ]
                  Node.profile "two" @"C:\t2" [ Node.source @"C:\Data\Nested" ] ]

        Assert.True(Result.isOk accepted)

module ``Duplicate profile names rejected`` =
    [<Fact>]
    let ``Duplicate names in configuration`` () =
        let outcome =
            validate
                [ Node.profile "Work" @"C:\t1" [ Node.source @"C:\a" ]
                  Node.profile "work" @"C:\t2" [ Node.source @"C:\b" ] ]

        match outcome with
        | Error error -> Assert.Equal(ConfigError.DuplicateName "work", error)
        | Ok _ -> Assert.Fail "expected a duplicate-name error"

module ``Retention policy configuration`` =
    let private withRetention fields =
        Node.profile "p" @"C:\t" [ Node.source @"C:\a" ]
        |> Node.withFields [ ("retention", Node.mapping fields) ]

    [<Fact>]
    let ``Retention count is not a valid integer`` () =
        for key in [ "keep_daily"; "keep_weekly"; "keep_monthly"; "keep_yearly" ] do
            validate [ withRetention [ (key, Node.scalar "abc") ] ]
            |> expectInvalid "p" $"retention field '{key}' has an invalid value 'abc' (expected a non-negative whole number)"

    [<Fact>]
    let ``Retention count is negative`` () =
        for key in [ "keep_daily"; "keep_weekly"; "keep_monthly"; "keep_yearly" ] do
            validate [ withRetention [ (key, Node.scalar "-1") ] ]
            |> expectInvalid "p" $"retention field '{key}' has an invalid value '-1' (expected a non-negative whole number)"

module ``Concurrency configuration`` =
    let private withConcurrency fields =
        Node.profile "p" @"C:\t" [ Node.source @"C:\a" ]
        |> Node.withFields [ ("concurrency", Node.mapping fields) ]

    let private limits (profile: Profile) =
        Profile.concurrency profile
        |> Option.map (fun c -> c.Scan |> Option.map ConcurrencyLimit.value, c.Transfer |> Option.map ConcurrencyLimit.value)

    [<Fact>]
    let ``Profile without concurrency configuration`` () =
        let profile = validate [ Node.profile "p" @"C:\t" [ Node.source @"C:\a" ] ] |> expectProfile
        Assert.True((Profile.concurrency profile).IsNone)

    [<Fact>]
    let ``Scan concurrency configured`` () =
        let profile = validate [ withConcurrency [ ("scan_concurrency", Node.scalar "4") ] ] |> expectProfile
        Assert.Equal(Some(Some 4, None), limits profile)

    [<Fact>]
    let ``Transfer concurrency configured`` () =
        let profile = validate [ withConcurrency [ ("transfer_concurrency", Node.scalar "8") ] ] |> expectProfile
        Assert.Equal(Some(None, Some 8), limits profile)

    [<Fact>]
    let ``Concurrency value is not a valid integer`` () =
        for key in [ "scan_concurrency"; "transfer_concurrency" ] do
            validate [ withConcurrency [ (key, Node.scalar "abc") ] ]
            |> expectInvalid "p" $"concurrency field '{key}' has an invalid value 'abc' (expected a positive whole number)"

    [<Fact>]
    let ``Concurrency value is zero or negative`` () =
        for key in [ "scan_concurrency"; "transfer_concurrency" ] do
            for value in [ "0"; "-3" ] do
                validate [ withConcurrency [ (key, Node.scalar value) ] ]
                |> expectInvalid "p" $"concurrency field '{key}' has an invalid value '{value}' (expected a positive whole number)"
