namespace Vara.Domain

open System
open System.IO

/// Where a restore writes: to a destination the user named, or back to each file's original source location.
[<RequireQualifiedAccess>]
type Destination =
    /// A file path for a single-file restore, a directory for a directory restore.
    | Explicit of path: string
    | InPlace

/// One file to write: a stored blob to a destination, with the modification time the file had at the source.
type RestoreWrite =
    { Source: ContentHash
      Path: MirrorPath
      Destination: string
      Size: int64
      ModifiedAt: DateTimeOffset }

/// Everything a restore will do, as data: the files to write, the destination files to remove because they did not
/// exist at the requested moment, and the links that have no content to write. A preview of a restore is this plan.
type RestorePlan =
    { Writes: RestoreWrite list
      Removals: string list
      Skipped: MirrorPath list
      /// The bytes of the writes. Removals add nothing, so they cannot distort throughput or ETA.
      TotalBytes: int64 }

module Restore =
    let private contentOf (version: FileVersion) : FileContent voption =
        match version.Row.Content with
        | RowContent.Regular content -> ValueSome content
        | RowContent.Linked _ -> ValueNone

    let private pathOf (version: FileVersion) : string = MirrorPath.value version.Row.RowPath

    let private totalBytes (writes: RestoreWrite list) : int64 =
        writes |> List.fold (fun total write -> total + write.Size) 0L

    let private isLive (version: FileVersion) : bool =
        match version.Row.Kind with
        | ChangeKind.Deleted -> false
        | ChangeKind.Added
        | ChangeKind.Changed
        | ChangeKind.Moved
        | ChangeKind.Linked -> true

    /// The plan to restore one version of a file. A link has no stored content, so it cannot be restored.
    let planFile (path: string) (destination: Destination) (version: FileVersion) : Result<RestorePlan, HistoryError> =
        match contentOf version with
        | ValueNone -> Error(HistoryError.NoContentToRestore path)
        | ValueSome content ->
            let target =
                match destination with
                | Destination.Explicit explicitPath -> explicitPath
                | Destination.InPlace -> SourceLocation.fromMirror (pathOf version)

            let write =
                { Source = content.Hash
                  Path = version.Row.RowPath
                  Destination = target
                  Size = content.Size
                  ModifiedAt = content.SourceModifiedAt }

            Ok
                { Writes = [ write ]
                  Removals = []
                  Skipped = []
                  TotalBytes = content.Size }

    /// The plan to make a directory match a moment: every file live under it then is written (a file deleted since
    /// is brought back, one added since is left out), a link is skipped and reported, and every file that is live
    /// now but was not then is removed. `versions` is every row of the manifest. Fails when nothing under the
    /// directory was ever recorded.
    let planDirectory
        (directory: string)
        (moment: Moment)
        (destination: Destination)
        (versions: FileVersion seq)
        : Result<RestorePlan, HistoryError> =
        let prefix = Prefix.ofDirectory directory
        let struct (atMoment, latest) = History.reduce moment versions

        let destinationOf (path: string) (subPath: string) : string =
            match destination with
            | Destination.InPlace -> SourceLocation.fromMirror path
            | Destination.Explicit root -> Path.Combine(root, subPath)

        let writes = ResizeArray<RestoreWrite>()
        let skipped = ResizeArray<MirrorPath>()
        let removals = ResizeArray<string>()

        let under (version: FileVersion) : bool = (Prefix.subPath prefix (pathOf version)).IsSome

        // Something was recorded under the directory when a row at the moment belongs to it, or a file in it is live
        // now (one added since the moment has no row at it).
        let anyTracked =
            atMoment.Values |> Seq.exists under
            || latest.Values |> Seq.exists (fun version -> isLive version && under version)

        for version in atMoment.Values do
            match Prefix.subPath prefix (pathOf version) with
            | ValueNone -> ()
            | ValueSome subPath ->
                if isLive version then
                    match contentOf version with
                    | ValueNone -> skipped.Add version.Row.RowPath
                    | ValueSome content ->
                        writes.Add
                            { Source = content.Hash
                              Path = version.Row.RowPath
                              Destination = destinationOf (pathOf version) subPath
                              Size = content.Size
                              ModifiedAt = content.SourceModifiedAt }

        for version in latest.Values do
            if isLive version then
                match Prefix.subPath prefix (pathOf version) with
                | ValueNone -> ()
                | ValueSome subPath ->
                    let wasLive =
                        match atMoment.TryGetValue(pathOf version) with
                        | true, earlier -> isLive earlier
                        | false, _ -> false

                    if not wasLive then
                        removals.Add(destinationOf (pathOf version) subPath)

        if not anyTracked then
            Error(HistoryError.NoSuchDirectory directory)
        else
            let ordered = writes |> Seq.sortBy _.Destination |> List.ofSeq

            Ok
                { Writes = ordered
                  Removals = removals |> Seq.sort |> List.ofSeq
                  Skipped = skipped |> Seq.sortBy MirrorPath.value |> List.ofSeq
                  TotalBytes = totalBytes ordered }

    /// Keeps only the removals whose destination exists: a file that is not there needs no removal, and is not
    /// counted in what the confirmation tells the user.
    let keepExistingRemovals (exists: string -> bool) (plan: RestorePlan) : RestorePlan =
        { plan with Removals = plan.Removals |> List.filter exists }

    /// Every destination the plan touches, to check each against the guard.
    let destinations (plan: RestorePlan) : string list =
        (plan.Writes |> List.map _.Destination) @ plan.Removals

    let private canonical (path: string) : string =
        let full =
            try
                Path.GetFullPath path
            with :? ArgumentException ->
                path

        full.TrimEnd('\\', '/')

    /// Refuses a destination that lies in the live mirror. Both paths are the real ones, after the shell followed
    /// every symlink and junction; this compares them canonically (`..` resolved, any letter case, with or without a
    /// trailing separator), so that no spelling of a path inside the mirror gets through.
    let guard (mirrorRoot: string) (resolvedDestination: string) : Result<unit, HistoryError> =
        let root = canonical mirrorRoot
        let candidate = canonical resolvedDestination

        let inside =
            String.Equals(candidate, root, StringComparison.OrdinalIgnoreCase)
            || candidate.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase)
            || candidate.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase)

        if inside then Error(HistoryError.DestinationInMirror resolvedDestination) else Ok()
