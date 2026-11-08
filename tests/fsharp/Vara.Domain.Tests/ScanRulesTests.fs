/// Scenario tests for the pure scan rules, named after the scenarios of openspec/specs/backup-execution/spec.md.
module Vara.Domain.Tests.ScanRulesTests

open Xunit
open Vara.Domain

let private mirror (path: string) : string voption =
    ScanRules.toMirrorPath path |> ValueOption.map MirrorPath.value

module ``Source exclusion rules honored`` =
    [<Fact>]
    let ``Excluded subfolder`` () =
        let excludes = ScanRules.normalizeExcludes [ "node_modules" ]
        Assert.True(ScanRules.isExcluded excludes "node_modules")
        Assert.True(ScanRules.isExcluded excludes "node_modules/pkg/index.js")
        Assert.False(ScanRules.isExcluded excludes "src/index.js")

    [<Fact>]
    let ``Nested exclude written with the non-native path separator`` () =
        // Entry and scanned path each written with both separator styles.
        for entry in [ "build/cache"; "build\\cache"; "build\\cache\\"; " build/cache/ " ] do
            let excludes = ScanRules.normalizeExcludes [ entry ]

            for scanned in [ "build/cache"; "build\\cache"; "build/cache/a.bin"; "build\\cache\\sub\\a.bin" ] do
                Assert.True(ScanRules.isExcluded excludes scanned, $"'{entry}' should exclude '{scanned}'")

            Assert.False(ScanRules.isExcluded excludes "build/cached/a.bin")
            Assert.False(ScanRules.isExcluded excludes "build")

    [<Fact>]
    let ``Exclude matching ignores letter case`` () =
        let excludes = ScanRules.normalizeExcludes [ "Build/Cache" ]
        Assert.True(ScanRules.isExcluded excludes "build\\cache\\a.bin")

    [<Fact>]
    let ``Glob matching sees the path with forward slashes`` () =
        let seen = ref ""

        let matcher (path: string) =
            seen.Value <- path
            true

        Assert.True(ScanRules.matchesGlobs matcher "docs\\guide\\a.md")
        Assert.Equal("docs/guide/a.md", seen.Value)

module ``One-to-one mirror of source state`` =
    [<Fact>]
    let ``Drive-letter-root source is scanned from the actual drive root`` () =
        Assert.Equal(ValueSome "D\\", mirror "D:\\")
        Assert.Equal(ValueSome "D\\Photos\\a.jpg", mirror "D:\\Photos\\a.jpg")
        Assert.Equal(ValueSome "c\\Users\\john\\file.txt", mirror "c:\\Users\\john\\file.txt")

    [<Fact>]
    let ``UNC source path produces no mirror entries`` () =
        Assert.Equal(ValueNone, mirror "\\\\srv\\share\\file.txt")
        Assert.Equal(ValueNone, mirror "//srv/share/file.txt")
        Assert.Equal(ValueNone, mirror "\\\\?\\C:\\Users\\file.txt")
