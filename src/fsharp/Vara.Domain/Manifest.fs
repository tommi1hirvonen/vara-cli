namespace Vara.Domain

open System

/// The recorded content of a regular file.
type FileContent =
    { Hash: ContentHash
      Size: int64
      SourceModifiedAt: DateTimeOffset
      Quick: QuickSignature voption }

/// What the manifest currently holds for a path that is not deleted. A link has no content hash, only a target.
[<RequireQualifiedAccess>]
type TrackedState =
    | Regular of content: FileContent
    | Linked of target: string voption

/// The tracked, non-deleted paths as of the latest snapshot, keyed case-insensitively by mirror path text.
type TrackedPaths = System.Collections.Generic.Dictionary<string, TrackedState>

/// The kind of change a manifest row records. Unchanged files get no row.
[<RequireQualifiedAccess>]
type ChangeKind =
    | Added
    | Changed
    | Moved
    | Deleted
    | Linked

/// What a manifest row holds: a file's content, or a link's target. A `Linked` row is never `Regular`;
/// the row builders keep that.
[<RequireQualifiedAccess>]
type RowContent =
    | Regular of content: FileContent
    | Linked of target: string voption

/// One historical entry for a path, produced by a completed operation and recorded under the run's snapshot.
type ManifestRow =
    { RowPath: MirrorPath
      PreviousPath: MirrorPath voption
      Content: RowContent
      Kind: ChangeKind
      RecordedAt: DateTimeOffset }
