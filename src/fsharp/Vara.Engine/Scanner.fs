namespace Vara.Engine

open System
open System.IO
open Microsoft.Extensions.FileSystemGlobbing
open Vara.Domain

/// Walks a profile's sources and yields what it finds as a stream, failures included. Reparse points (symlinks,
/// junctions) are reported and never followed. A directory on a source's literal exclude list is not enumerated, so
/// nothing beneath it is scanned or fails. Anything unreadable becomes a `ScanFailure` and the walk goes on.
module Scanner =
    let private isIoFailure (ex: exn) : bool =
        match ex with
        | :? IOException
        | :? UnauthorizedAccessException -> true
        | _ -> false

    /// The mirror path of a path, or the path itself where there is none (UNC), which is what a failure's coverage
    /// then matches against: nothing tracked, so nothing is shielded by accident.
    let private coverOf (path: string) : MirrorPath =
        match ScanRules.toMirrorPath path with
        | ValueSome mirror -> mirror
        | ValueNone -> MirrorPath.ofString path

    let private failure (reported: string) (path: string) (reason: ScanFailureReason) : ScanEntry =
        ScanEntry.Failure
            { Reported = reported
              Covers = coverOf path
              Reason = reason }

    let private linkTarget (path: string) : string voption =
        try
            match FileInfo(path).LinkTarget with
            | null -> ValueNone
            | target -> ValueSome target
        with ex when isIoFailure ex ->
            ValueNone

    /// The entry for one path found by the walk (or a source that is itself a file). `relative` is the text a
    /// failure reports. A path with no mirror location, such as a UNC path, cannot be mirrored: it fails here.
    let private entryOf (path: string) (relative: string) (attributes: FileAttributes) : ScanEntry =
        match ScanRules.toMirrorPath path with
        | ValueNone -> failure path path ScanFailureReason.NoMirrorPath
        | ValueSome mirror ->
            if attributes.HasFlag FileAttributes.ReparsePoint then
                ScanEntry.Link
                    { LinkPath = mirror
                      LinkSource = SourcePath.ofString path
                      LinkTarget = linkTarget path }
            else
                try
                    let info = FileInfo path

                    ScanEntry.File
                        { FilePath = mirror
                          FileSource = SourcePath.ofString path
                          Size = info.Length
                          ModifiedAt = DateTimeOffset info.LastWriteTimeUtc }
                with ex when isIoFailure ex ->
                    failure relative path ScanFailureReason.UnreadableEntry

    let private globMatcher (source: Source) : (string -> bool) voption =
        if List.isEmpty source.IncludeGlobs && List.isEmpty source.ExcludeGlobs then
            ValueNone
        else
            let matcher = Matcher(StringComparison.OrdinalIgnoreCase)
            matcher.AddIncludePatterns(if List.isEmpty source.IncludeGlobs then [ "**/*" ] else source.IncludeGlobs)

            if not (List.isEmpty source.ExcludeGlobs) then
                matcher.AddExcludePatterns source.ExcludeGlobs

            ValueSome(fun (path: string) -> matcher.Match(path).HasMatches)

    /// What the walk finds: a path to describe, or a failure already described.
    [<Struct>]
    type private WalkItem =
        | Found of path: string * attributes: FileAttributes
        | Failed of failure: ScanEntry

    /// Every file and reparse point beneath a directory, with its attributes; plain directories are entered, not
    /// yielded. A directory that cannot be enumerated yields one failure for itself, not one per file it holds.
    let rec private walk (directory: string) (source: Source) (root: string) (excludes: string list) : seq<WalkItem> =
        seq {
            let listing =
                try
                    Ok(Directory.EnumerateFileSystemEntries(directory) |> Seq.toArray)
                with ex when isIoFailure ex ->
                    Error()

            match listing with
            | Error() ->
                yield Failed(failure (Path.GetRelativePath(root, directory)) directory ScanFailureReason.UnreadableDirectory)
            | Ok entries ->
                for entry in entries do
                    let attributes =
                        try
                            ValueSome(File.GetAttributes entry)
                        with ex when isIoFailure ex ->
                            ValueNone

                    match attributes with
                    | ValueNone ->
                        yield Failed(failure (Path.GetRelativePath(root, entry)) entry ScanFailureReason.UnreadableEntry)
                    | ValueSome attributes ->
                        if attributes.HasFlag FileAttributes.ReparsePoint then
                            // Reported, never entered, whether it links to a file or a directory.
                            yield Found(entry, attributes)
                        elif attributes.HasFlag FileAttributes.Directory then
                            if source.Recursive then
                                // Checked before descending, so an excluded directory is never enumerated.
                                if not (ScanRules.isExcluded excludes (Path.GetRelativePath(root, entry))) then
                                    yield! walk entry source root excludes
                        else
                            yield Found(entry, attributes)
        }

    let private scanSource (source: Source) : seq<ScanEntry> =
        seq {
            let path = AbsolutePath.value source.Path

            if File.Exists path then
                // A source may point straight at one file.
                let fileName = Path.GetFileName path

                match (try ValueSome(File.GetAttributes path) with ex when isIoFailure ex -> ValueNone) with
                | ValueSome attributes -> yield entryOf path fileName attributes
                | ValueNone -> yield failure fileName path ScanFailureReason.UnreadableEntry
            elif not (Directory.Exists path) then
                yield failure path path ScanFailureReason.SourceUnavailable
            else
                let matcher = globMatcher source
                let excludes = ScanRules.normalizeExcludes source.Excludes
                // TrimEndingDirectorySeparator keeps a drive root such as `D:\` whole, where a bare trim would leave
                // `D:`, which Windows resolves against the current directory of that drive.
                let root = Path.TrimEndingDirectorySeparator path

                for item in walk root source root excludes do
                    match item with
                    | Failed entry -> yield entry
                    | Found(entryPath, attributes) ->
                        let relative = Path.GetRelativePath(root, entryPath)

                        let reported =
                            not (ScanRules.isExcluded excludes relative)
                            && (match matcher with
                                | ValueNone -> true
                                | ValueSome matches -> ScanRules.matchesGlobs matches relative)

                        if reported then
                            yield entryOf entryPath relative attributes
        }

    /// The scan of every source, in order, as a lazy stream. Enumerate it once.
    let scan (sources: Source list) : seq<ScanEntry> = sources |> Seq.collect scanSource
