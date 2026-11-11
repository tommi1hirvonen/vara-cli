namespace Vara.Engine

open System
open System.IO
open System.IO.Enumeration
open Microsoft.Extensions.FileSystemGlobbing
open Vara.Domain

/// Walks a profile's sources and yields what it finds as a stream, failures included. Reparse points (symlinks,
/// junctions) are reported and never followed. A directory on a source's literal exclude list is not enumerated, so
/// nothing beneath it is scanned or fails. Anything unreadable becomes a `ScanFailure` and the walk goes on.
module Scanner =
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
        with IoFailure _ ->
            ValueNone

    /// What the scan records of a file's content state: its size and modification time.
    [<Struct>]
    type private FileStamp =
        { StampSize: int64
          StampModified: DateTimeOffset }

    /// The stamp of a file that was not listed, read from the file itself. None when it cannot be read.
    let private stampOf (path: string) : FileStamp voption =
        try
            let info = FileInfo path

            ValueSome
                { StampSize = info.Length
                  StampModified = DateTimeOffset info.LastWriteTimeUtc }
        with IoFailure _ ->
            ValueNone

    /// The entry for one path found by the walk (or a source that is itself a file). `relative` is the text a
    /// failure reports. A path with no mirror location, such as a UNC path, cannot be mirrored: it fails here. A file
    /// has its `stamp` unless that could not be read, which fails it; a link has none.
    let private entryOf
        (path: string)
        (relative: string)
        (attributes: FileAttributes)
        (stamp: FileStamp voption)
        : ScanEntry =
        match ScanRules.toMirrorPath path with
        | ValueNone -> failure path path ScanFailureReason.NoMirrorPath
        | ValueSome mirror ->
            if attributes.HasFlag FileAttributes.ReparsePoint then
                ScanEntry.Link
                    { LinkPath = mirror
                      LinkSource = SourcePath.ofString path
                      LinkTarget = linkTarget path }
            else
                match stamp with
                | ValueSome stamp ->
                    ScanEntry.File
                        { FilePath = mirror
                          FileSource = SourcePath.ofString path
                          Size = stamp.StampSize
                          ModifiedAt = stamp.StampModified }
                | ValueNone -> failure relative path ScanFailureReason.UnreadableEntry

    let private globMatcher (source: Source) : (string -> bool) voption =
        if List.isEmpty source.IncludeGlobs && List.isEmpty source.ExcludeGlobs then
            ValueNone
        else
            let matcher = Matcher StringComparison.OrdinalIgnoreCase
            matcher.AddIncludePatterns(if List.isEmpty source.IncludeGlobs then [ "**/*" ] else source.IncludeGlobs)

            if not (List.isEmpty source.ExcludeGlobs) then
                matcher.AddExcludePatterns source.ExcludeGlobs

            ValueSome(fun (path: string) -> matcher.Match(path).HasMatches)

    /// One entry of a directory listing, with everything the scan needs of it. The file system reports all of this
    /// while it lists the directory, so no entry is opened or queried again afterwards.
    [<Struct>]
    type private Listed =
        { EntryPath: string
          /// The path beneath the source, with the system's separator.
          EntryRelative: string
          EntryAttributes: FileAttributes
          EntryStamp: FileStamp }

    /// Hidden and system entries are backed up like any other, and a directory that cannot be listed raises, so that
    /// it is reported. The defaults of this API skip the first and hide the second.
    let private listingOptions =
        EnumerationOptions(AttributesToSkip = enum<FileAttributes> 0, IgnoreInaccessible = false)

    /// Lists one directory in a single pass over it. Raises where the directory cannot be listed.
    let private list (directory: string) (relativeDirectory: string) : Listed[] =
        let describe =
            FileSystemEnumerable<Listed>.FindTransform(fun entry ->
                { EntryPath = entry.ToSpecifiedFullPath()
                  EntryRelative = Path.Join(relativeDirectory.AsSpan(), entry.FileName)
                  EntryAttributes = entry.Attributes
                  EntryStamp =
                    { StampSize = entry.Length
                      StampModified = entry.LastWriteTimeUtc } })

        FileSystemEnumerable<Listed>(directory, describe, listingOptions)
        |> Seq.toArray

    /// What the walk finds: an entry to describe, or a failure already described.
    [<Struct>]
    type private WalkItem =
        | Found of listed: Listed
        | Failed of failure: ScanEntry

    /// Every file and reparse point beneath a directory, with its attributes; plain directories are entered, not
    /// yielded. A directory that cannot be enumerated yields one failure for itself, not one per file it holds.
    /// `relativeDirectory` is the directory's path beneath the source, empty for the source itself.
    let rec private walk (directory: string) (relativeDirectory: string) (source: Source) (excludes: string list) : seq<WalkItem> =
        seq {
            let listing =
                try
                    Ok(list directory relativeDirectory)
                with IoFailure _ ->
                    Error()

            match listing with
            | Error() ->
                let reported = if relativeDirectory.Length = 0 then "." else relativeDirectory
                yield Failed(failure reported directory ScanFailureReason.UnreadableDirectory)
            | Ok entries ->
                for entry in entries do
                    if entry.EntryAttributes.HasFlag FileAttributes.ReparsePoint then
                        // Reported, never entered, whether it links to a file or a directory.
                        yield Found entry
                    elif entry.EntryAttributes.HasFlag FileAttributes.Directory then
                        if source.Recursive then
                            // Checked before descending, so an excluded directory is never enumerated.
                            if not (ScanRules.isExcluded excludes entry.EntryRelative) then
                                yield! walk entry.EntryPath entry.EntryRelative source excludes
                    else
                        yield Found entry
        }

    let private scanSource (source: Source) : seq<ScanEntry> =
        seq {
            let path = AbsolutePath.value source.Path

            if File.Exists path then
                // A source may point straight at one file.
                let fileName = Path.GetFileName path

                match (try ValueSome(File.GetAttributes path) with IoFailure _ -> ValueNone) with
                | ValueSome attributes ->
                    let stamp = if attributes.HasFlag FileAttributes.ReparsePoint then ValueNone else stampOf path
                    yield entryOf path fileName attributes stamp
                | ValueNone -> yield failure fileName path ScanFailureReason.UnreadableEntry
            elif not (Directory.Exists path) then
                yield failure path path ScanFailureReason.SourceUnavailable
            else
                let matcher = globMatcher source
                let excludes = ScanRules.normalizeExcludes source.Excludes
                // TrimEndingDirectorySeparator keeps a drive root such as `D:\` whole, where a bare trim would leave
                // `D:`, which Windows resolves against the current directory of that drive.
                let root = Path.TrimEndingDirectorySeparator path

                for item in walk root "" source excludes do
                    match item with
                    | Failed entry -> yield entry
                    | Found listed ->
                        let reported =
                            not (ScanRules.isExcluded excludes listed.EntryRelative)
                            && match matcher with
                               | ValueNone -> true
                               | ValueSome matches -> ScanRules.matchesGlobs matches listed.EntryRelative

                        if reported then
                            yield entryOf listed.EntryPath listed.EntryRelative listed.EntryAttributes (ValueSome listed.EntryStamp)
        }

    /// The scan of every source, in order, as a lazy stream. Enumerate it once.
    let scan (sources: Source list) : seq<ScanEntry> = sources |> Seq.collect scanSource
