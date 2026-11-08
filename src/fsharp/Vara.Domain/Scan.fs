namespace Vara.Domain

open System

/// Why part of a source could not be scanned. Each reason keeps its previous manifest state for the run.
[<RequireQualifiedAccess>]
type ScanFailureReason =
    | UnreadableEntry
    | UnreadableDirectory
    | SourceUnavailable
    /// The entry's source path has no mirror path, for example a UNC path.
    | NoMirrorPath

type ScannedFile =
    { FilePath: MirrorPath
      FileSource: SourcePath
      Size: int64
      ModifiedAt: DateTimeOffset }

type ScannedLink =
    { LinkPath: MirrorPath
      LinkSource: SourcePath
      LinkTarget: string voption }

/// A failure to scan something. `Reported` is the text the run summary shows (a path relative to its source);
/// `Covers` is the mirror path whose tracked entries the failure shields from deletion, with everything beneath it.
type ScanFailure =
    { Reported: string
      Covers: MirrorPath
      Reason: ScanFailureReason }

/// What a scan yields, one at a time. A failure arrives in the stream next to the entries it affects, so a consumer
/// never has to wait for the walk to end to learn what could not be read.
[<Struct; RequireQualifiedAccess>]
type ScanEntry =
    | File of file: ScannedFile
    | Link of link: ScannedLink
    | Failure of failure: ScanFailure
