namespace Vara.Core.Abstractions;

/// <summary>
/// The content-addressed, deduplicated store backing a profile's live mirror and
/// version history. One instance is scoped to one profile's target root.
/// </summary>
public interface IContentStore
{
    /// <summary>
    /// Probes whether the target volume supports hardlinks by creating and removing a
    /// throwaway link. Must be called once at the start of a run before other members
    /// are used; the result is cached for the lifetime of this instance.
    /// </summary>
    bool ProbeHardlinkSupport();

    /// <summary>
    /// Whether the last <see cref="ProbeHardlinkSupport"/> call determined hardlinks are supported.
    /// </summary>
    bool SupportsHardlinks { get; }

    /// <summary>
    /// Streams <paramref name="content"/> into the store, computing its content hash and
    /// storing it as the canonical blob for that hash if not already present (dedup).
    /// Returns the computed hash and the byte count read.
    /// </summary>
    (string Hash, long Size) StoreFromStream(Stream content);

    /// <summary>
    /// Returns whether a blob for <paramref name="hash"/> already exists in the store.
    /// </summary>
    bool HasContent(string hash);

    /// <summary>
    /// Atomically places the blob for <paramref name="hash"/> at <paramref name="mirrorRelativePath"/>,
    /// replacing any existing entry there. Uses a hardlink when supported, otherwise a real copy.
    /// If hardlinks are supported at the volume level but creating one for this specific blob
    /// fails (for example, because the blob already has the maximum number of hard links a
    /// single file can have on the target filesystem), falls back to a real copy for just this
    /// placement rather than failing the operation.
    /// </summary>
    void PlaceAtMirrorPath(string hash, string mirrorRelativePath);

    /// <summary>
    /// Relocates an existing mirror entry from one relative path to another (a rename),
    /// without re-transferring content. Used for detected moves; works regardless of
    /// hardlink support.
    /// </summary>
    void MoveMirrorEntry(string fromRelativePath, string toRelativePath);

    /// <summary>
    /// Removes a mirror entry (used for deletions). The blob itself, if any, remains in
    /// the store until garbage-collected.
    /// </summary>
    void RemoveFromMirror(string mirrorRelativePath);

    /// <summary>
    /// Permanently deletes the blob for <paramref name="hash"/> from the store (used by pruning's GC step).
    /// </summary>
    void DeleteContent(string hash);

    /// <summary>
    /// Removes any leftover staging files from an interrupted previous run.
    /// </summary>
    void CleanupOrphanedTemp();

    /// <summary>
    /// Copies the blob for <paramref name="hash"/> to an arbitrary destination path
    /// (not necessarily within the mirror) - used by the snapshot-history capability's
    /// restore command to extract a historical version without touching the live mirror.
    /// </summary>
    void ExtractTo(string hash, string destinationAbsolutePath);

    /// <summary>
    /// Enumerates every content hash physically present in the store. Used together with
    /// <see cref="Vara.Core.Abstractions.ISnapshotRepository.GetAllReferencedContentHashes"/> to
    /// compute which blobs are safe to garbage-collect after pruning.
    /// </summary>
    IReadOnlySet<string> ListAllStoredHashes();
}
