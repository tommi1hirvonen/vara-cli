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
    /// Returns the computed hash and the byte count read. When <paramref name="onBytesWritten"/>
    /// is given, it is invoked with each chunk's size as it is copied, so a caller can report
    /// progress incrementally during a large file's transfer rather than only once it completes.
    /// </summary>
    (string Hash, long Size) StoreFromStream(Stream content, Action<long>? onBytesWritten = null);

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
    /// placement rather than failing the operation. When a real copy is performed and
    /// <paramref name="onBytesCopied"/> is given, it is invoked with each chunk's size as it is
    /// copied, so a caller can report progress incrementally during a large fallback copy. A
    /// hardlinked placement is marked read-only, since it is the same physical file as its
    /// content-store blob; a copy-fallback placement is left writable. <paramref name="previousContentHash"/>,
    /// when given, identifies the content previously at <paramref name="mirrorRelativePath"/> (if
    /// any) - used to restore the read-only attribute on that content's own blob file after the
    /// overwrite, since overwriting a hardlinked mirror entry must clear that attribute first, and
    /// on NTFS it is shared across every hardlink to the same data (the blob's own canonical name,
    /// and any other mirror path still deduplicated against it), not just the one being replaced.
    /// </summary>
    void PlaceAtMirrorPath(string hash, string mirrorRelativePath, Action<long>? onBytesCopied = null, string? previousContentHash = null);

    /// <summary>
    /// Relocates an existing mirror entry from one relative path to another (a rename),
    /// without re-transferring content. Used for detected moves; works regardless of
    /// hardlink support.
    /// </summary>
    void MoveMirrorEntry(string fromRelativePath, string toRelativePath);

    /// <summary>
    /// Removes a mirror entry (used for deletions). The blob itself, if any, remains in
    /// the store until garbage-collected. <paramref name="hash"/> identifies that content -
    /// used to restore the read-only attribute on its blob file after removal, since removing
    /// a hardlinked mirror entry must clear that attribute first, and on NTFS it is shared
    /// across every hardlink to the same data (the blob's own canonical name, and any other
    /// mirror path still deduplicated against it), not just the entry being removed.
    /// </summary>
    void RemoveFromMirror(string mirrorRelativePath, string hash);

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
    /// Replaces any existing file already at <paramref name="destinationAbsolutePath"/>.
    /// When <paramref name="onBytesCopied"/> is given, it is invoked with each chunk's size
    /// as it is copied, so a caller can report progress incrementally during a large restore.
    /// </summary>
    void ExtractTo(string hash, string destinationAbsolutePath, Action<long>? onBytesCopied = null);

    /// <summary>
    /// Deletes a file previously written by <see cref="ExtractTo"/> at an arbitrary destination
    /// path (not necessarily within the mirror) - used by the snapshot-history capability's
    /// recursive directory restore to remove destination content that is tracked as currently
    /// live but was not live as of the requested point in time. A no-op when no file exists at
    /// <paramref name="absolutePath"/>.
    /// </summary>
    void RemoveExtractedFile(string absolutePath);

    /// <summary>
    /// Opens a readable stream over the blob for <paramref name="hash"/>, for callers that
    /// need to read a version's content directly (e.g. streaming it to standard output, or
    /// diffing two versions) rather than copying it to a destination file via
    /// <see cref="ExtractTo"/>. The caller owns and disposes the returned stream.
    /// </summary>
    Stream OpenRead(string hash);

    /// <summary>
    /// Whether <paramref name="absolutePath"/> resolves to a location inside this store's
    /// mirror root. Used by the snapshot-history capability's restore command to refuse
    /// restoring into the live mirror, which would corrupt it.
    /// </summary>
    bool IsWithinMirror(string absolutePath);

    /// <summary>
    /// Whether a file already exists at <paramref name="absolutePath"/>. Used by the
    /// snapshot-history capability's restore command to guard against silently overwriting
    /// an existing destination without explicit user confirmation.
    /// </summary>
    bool TargetExists(string absolutePath);

    /// <summary>
    /// Enumerates every content hash physically present in the store. Used together with
    /// <see cref="Vara.Core.Abstractions.ISnapshotRepository.GetAllReferencedContentHashes"/> to
    /// compute which blobs are safe to garbage-collect after pruning.
    /// </summary>
    IReadOnlySet<string> ListAllStoredHashes();
}
