namespace Vara.Domain

/// A scanned entry that differs from the manifest. A file with no row is `Add`: a possible move target.
/// A file with a row but another size or time is `Change`: an edit, never a move. A link that is new, or that
/// replaces a regular file, is `Link`.
[<Struct; RequireQualifiedAccess>]
type PendingChange =
    | Add of added: ScannedFile
    | Change of changed: ScannedFile
    | Link of link: ScannedLink

/// What a transfer replaces: nothing for an added file; for a changed file, the content it supersedes, if any
/// (none when the path was a link).
[<RequireQualifiedAccess>]
type TransferKind =
    | Added
    | Changed of superseded: ContentHash voption

/// Copies a file's content from the source into the store and the mirror.
type Transfer =
    { Source: ScannedFile
      TransferKind: TransferKind }

/// Relocates a mirror entry and records the old path as deleted and the new one as moved.
type MoveOperation =
    { MoveTo: MirrorPath
      MoveFrom: MirrorPath
      Moved: FileContent }

/// Removes a mirror entry (a link has none) and records the path as deleted.
type DeleteOperation =
    { DeletePath: MirrorPath
      Deleted: TrackedState }

/// Records a link. `Superseded` is the content of the regular file the link replaces, so its mirror copy is removed.
type LinkOperation =
    { LinkAt: MirrorPath
      LinkTo: string voption
      Superseded: ContentHash voption }

/// An operation that never reads source content.
[<RequireQualifiedAccess>]
type MetadataOperation =
    | Move of move: MoveOperation
    | Delete of delete: DeleteOperation
    | Link of link: LinkOperation

/// Every operation of a run. The two lists run in separate stages, so no consumer has to match a kind it cannot
/// meet: metadata operations in order, then transfers in a bounded pool.
type BackupPlan =
    { Metadata: MetadataOperation list
      Transfers: Transfer list
      /// The bytes the transfers will copy; metadata operations add none.
      TotalBytes: int64 }

/// A plan's counts, for a dry run: what a real run would add, change, move and delete, and what it could not scan.
type PlanSummary =
    { Added: int
      Changed: int
      Moved: int
      Deleted: int
      PlannedBytes: int64
      Unscanned: string list }
