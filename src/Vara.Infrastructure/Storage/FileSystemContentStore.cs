using Vara.Core.Abstractions;
using Vara.Core.IO;
using Vara.Infrastructure.Interop;

namespace Vara.Infrastructure.Storage;

/// <summary>
/// Content-addressed, deduplicated store for a single profile's target root: the
/// live mirror lives directly under <paramref name="targetRoot"/>, with a hidden
/// <c>.vara\</c> sibling folder holding the content-addressed blob store and a
/// staging area for atomic writes. See design.md's "Storage layout" and
/// "Atomicity and crash safety" decisions.
/// </summary>
public sealed class FileSystemContentStore : IContentStore
{
    // Applied to both sides of a streamed copy (see StoreFromStream and the manual copy in
    // PlaceAtMirrorPath) so that progress-reporting chunk sizes requested by a reader (e.g. the
    // hasher's own internal read loop) don't shrink the actual disk read/write size below what
    // Stream.CopyTo's own default buffer achieved before this change.
    private const int StreamBufferSize = 1024 * 1024;

    private readonly string _mirrorRoot;
    private readonly string _versionsRoot;
    private readonly string _tempRoot;
    private readonly IHasher _hasher;
    private bool? _supportsHardlinks;
    private bool _forceNextHardlinkFailureForTesting;

    /// <param name="createIfMissing">
    /// When <see langword="true"/> (the default), eagerly creates the mirror, versions,
    /// and temp directories under <paramref name="targetRoot"/> if they don't already
    /// exist. When <see langword="false"/>, no directory is created - used by read-only
    /// commands (see the fix-readonly-command-side-effects change) so that resolving a
    /// content store for a profile that has never completed a backup run does not itself
    /// materialize its <c>.vara\</c> structure as a side effect. A read-only caller
    /// should only ever reach members that read existing content (for example
    /// <see cref="OpenRead"/>, <see cref="ExtractTo"/>), which - for a profile with no
    /// recorded state - are never invoked in the first place.
    /// </param>
    public FileSystemContentStore(string targetRoot, IHasher hasher, bool createIfMissing = true)
    {
        _mirrorRoot = targetRoot;
        _versionsRoot = Path.Combine(targetRoot, ".vara", "versions");
        _tempRoot = Path.Combine(targetRoot, ".vara", "tmp");
        _hasher = hasher;

        if (createIfMissing)
        {
            Directory.CreateDirectory(_mirrorRoot);
            Directory.CreateDirectory(_versionsRoot);
            Directory.CreateDirectory(_tempRoot);
        }
    }

    public bool SupportsHardlinks =>
        _supportsHardlinks ?? throw new InvalidOperationException($"{nameof(ProbeHardlinkSupport)} must be called before the content store is used.");

    public bool ProbeHardlinkSupport()
    {
        var sourceFile = Path.Combine(_tempRoot, $"probe-src-{Guid.NewGuid():N}.tmp");
        var linkFile = Path.Combine(_tempRoot, $"probe-link-{Guid.NewGuid():N}.tmp");

        try
        {
            File.WriteAllBytes(sourceFile, [0]);
            Kernel32.CreateHardLink(linkFile, sourceFile);
            _supportsHardlinks = true;
        }
        catch (IOException)
        {
            _supportsHardlinks = false;
        }
        catch (UnauthorizedAccessException)
        {
            _supportsHardlinks = false;
        }
        finally
        {
            TryDelete(sourceFile);
            TryDelete(linkFile);
        }

        return _supportsHardlinks.Value;
    }

    public (string Hash, long Size) StoreFromStream(Stream content, Action<long>? onBytesWritten = null)
    {
        var tempPath = Path.Combine(_tempRoot, Guid.NewGuid().ToString("N"));
        string hash;
        long size;

        // Single forward pass: as the hasher reads from the tee, each chunk is simultaneously
        // written to the temp file and reported via onBytesWritten, replacing the previous
        // "CopyTo temp file, then reopen it to hash it" two-pass approach
        // (stream-large-file-transfer-progress change's design.md). `content` is not owned by
        // this method (matching the previous CopyTo-based implementation, which never disposed
        // it either), so it's wrapped for buffered reads without disposing the wrapper -
        // BufferedStream.Dispose would otherwise cascade into disposing the caller's stream.
        var bufferedSource = new BufferedStream(content, StreamBufferSize);
        using (var tempFile = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write))
        using (var bufferedSink = new BufferedStream(tempFile, StreamBufferSize))
        using (var tee = new TeeStream(bufferedSource, bufferedSink, onBytesWritten))
        {
            hash = _hasher.ComputeHash(tee);
            size = tee.TotalBytesCopied;
        }

        var blobPath = BlobPath(hash);
        if (File.Exists(blobPath))
        {
            // Content already stored under this hash (dedup) - discard the redundant copy.
            File.Delete(tempPath);
        }
        else
        {
            Directory.CreateDirectory(Path.GetDirectoryName(blobPath)!);
            try
            {
                File.Move(tempPath, blobPath);
            }
            catch (IOException) when (File.Exists(blobPath))
            {
                // Benign race: a concurrent StoreFromStream call for the same content
                // won and created the blob first. Our copy is now redundant.
                File.Delete(tempPath);
            }
        }

        return (hash, size);
    }

    public bool HasContent(string hash) => File.Exists(BlobPath(hash));

    public Stream OpenRead(string hash)
    {
        var blobPath = BlobPath(hash);
        if (!File.Exists(blobPath))
        {
            throw new FileNotFoundException($"No stored content for hash '{hash}'.", blobPath);
        }

        return new FileStream(blobPath, FileMode.Open, FileAccess.Read, FileShare.Read);
    }

    public void PlaceAtMirrorPath(string hash, string mirrorRelativePath, Action<long>? onBytesCopied = null, string? previousContentHash = null)
    {
        var blobPath = BlobPath(hash);
        if (!File.Exists(blobPath))
        {
            throw new FileNotFoundException($"No stored content for hash '{hash}'.", blobPath);
        }

        var mirrorPath = Path.Combine(_mirrorRoot, mirrorRelativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(mirrorPath)!);

        var stagingPath = Path.Combine(_tempRoot, Guid.NewGuid().ToString("N"));
        bool placedViaHardlink;
        if (SupportsHardlinks)
        {
            // The hardlink attempt is retried-then-fallback per placement rather than
            // memoized per blob: a blob's link count isn't a stable fact like volume-level
            // hardlink support is, since other mirror paths referencing it can later be
            // removed (moves, deletes, pruning), letting a future attempt succeed again.
            try
            {
                if (_forceNextHardlinkFailureForTesting)
                {
                    _forceNextHardlinkFailureForTesting = false;
                    throw new IOException("Simulated hardlink failure for testing.");
                }

                Kernel32.CreateHardLink(stagingPath, blobPath);
                placedViaHardlink = true;
            }
            catch (IOException)
            {
                // Most notably NTFS's 1024-hard-link-per-file cap: once a blob is already
                // referenced by the maximum number of hard links, creating another one
                // fails. Falling back to a real copy for just this placement keeps the run
                // succeeding instead of permanently failing every mirror path beyond the cap.
                CopyWithProgress(blobPath, stagingPath, onBytesCopied);
                placedViaHardlink = false;
            }
        }
        else
        {
            CopyWithProgress(blobPath, stagingPath, onBytesCopied);
            placedViaHardlink = false;
        }

        // A hardlinked mirror entry IS the content-store blob (same physical file): an
        // in-place edit through it would silently rewrite the blob, corrupting that
        // content's historical version record and every other mirror path or version
        // sharing the same hash. Marking it read-only turns a naive in-place overwrite
        // into a failure instead of silent corruption. A copy-fallback entry is an
        // independent physical copy with no such risk, so it stays writable - explicitly
        // cleared here (not merely assumed) since a given mirror path's placement kind can
        // change between runs (see the hard-link-limit fallback/recovery requirement).
        var stagingAttributes = File.GetAttributes(stagingPath);
        File.SetAttributes(stagingPath, placedViaHardlink
            ? stagingAttributes | FileAttributes.ReadOnly
            : stagingAttributes & ~FileAttributes.ReadOnly);

        // File.Move(overwrite: true) throws UnauthorizedAccessException if the existing
        // destination is read-only (verified empirically - it does not just replace a
        // read-only file), so a "Changed" re-placement of an already-hardlinked mirror path
        // would otherwise fail every time. Clearing it first lets the overwrite proceed;
        // the moved-in staged file's own attribute (set above) becomes the destination's
        // final attribute, not the cleared one.
        ClearReadOnlyIfPresent(mirrorPath);

        // Atomic swap: rename the staged file into place, replacing any existing mirror
        // entry in one filesystem operation - never an in-place overwrite.
        File.Move(stagingPath, mirrorPath, overwrite: true);

        // Re-assert read-only on the final path for every hardlink placement, rather than
        // relying solely on the staged file's attribute (set above) surviving the clear
        // above. When the mirror path being replaced was already a hardlink to this exact
        // same blob, the staged file, mirrorPath, and the blob are all one underlying file -
        // ClearReadOnlyIfPresent(mirrorPath) then un-marks the staged file too (NTFS shares
        // FileAttributes.ReadOnly per underlying file, not per hardlink name), so without this
        // re-assert the moved-in file - and the blob itself - would land permanently writable.
        // This is reachable through an "Add" placement with no previousContentHash (e.g. a run
        // recovering from an interruption that wrote the mirror entry but never recorded it in
        // the manifest, so the path is re-planned as newly added). A no-op in the overwhelming
        // common case where the attribute survived the clear untouched.
        if (placedViaHardlink)
        {
            EnsureReadOnlyIfPresent(mirrorPath);
        }

        // The clear above (if it ran) affects every hardlink to the previous content, not
        // just mirrorPath - including that content's own canonical blob file, and any other
        // mirror path still deduplicated against it (NTFS shares FileAttributes.ReadOnly per
        // underlying file, not per hardlink name - verified empirically; see design.md).
        // Restoring it via the blob's own name re-protects all of those survivors uniformly.
        // Safe to attempt unconditionally: a no-op if the previous entry was a copy-fallback
        // placement (whose attribute clear never touched the blob), or if the blob has since
        // been pruned.
        if (previousContentHash is not null)
        {
            EnsureReadOnlyIfPresent(BlobPath(previousContentHash));
        }
    }

    /// <summary>
    /// Clears <see cref="FileAttributes.ReadOnly"/> on <paramref name="path"/> if it is
    /// currently set, so a subsequent <see cref="File.Move(string, string, bool)"/> overwrite
    /// or <see cref="File.Delete(string)"/> against it does not throw
    /// <see cref="UnauthorizedAccessException"/>. No-ops if the path doesn't exist or isn't
    /// read-only.
    /// </summary>
    private static void ClearReadOnlyIfPresent(string path)
    {
        if (!File.Exists(path))
        {
            return;
        }

        var attributes = File.GetAttributes(path);
        if (attributes.HasFlag(FileAttributes.ReadOnly))
        {
            File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
        }
    }

    /// <summary>
    /// Sets <see cref="FileAttributes.ReadOnly"/> on <paramref name="path"/> if it isn't
    /// already set - the inverse of <see cref="ClearReadOnlyIfPresent"/>, used to restore
    /// protection on a content-store blob after a clear-and-mutate elsewhere shared that
    /// same underlying file's attribute (see design.md). No-ops if the path doesn't exist or
    /// is already read-only.
    /// </summary>
    private static void EnsureReadOnlyIfPresent(string path)
    {
        if (!File.Exists(path))
        {
            return;
        }

        var attributes = File.GetAttributes(path);
        if (!attributes.HasFlag(FileAttributes.ReadOnly))
        {
            File.SetAttributes(path, attributes | FileAttributes.ReadOnly);
        }
    }

    /// <summary>
    /// Streams <paramref name="sourcePath"/> to <paramref name="destinationPath"/>, reporting
    /// each chunk's size via <paramref name="onBytesCopied"/> as it is copied - replaces a
    /// plain <see cref="File.Copy(string, string)"/> so that a large fallback copy (no
    /// hardlink support, or a blob's hard-link limit reached) reports incremental progress
    /// instead of being invisible until it completes (stream-large-file-transfer-progress
    /// change's design.md).
    /// </summary>
    private static void CopyWithProgress(string sourcePath, string destinationPath, Action<long>? onBytesCopied)
    {
        using var source = new BufferedStream(File.OpenRead(sourcePath), StreamBufferSize);
        using var destination = new BufferedStream(new FileStream(destinationPath, FileMode.CreateNew, FileAccess.Write), StreamBufferSize);
        using var tee = new TeeStream(source, destination, onBytesCopied);
        tee.CopyTo(Stream.Null);
    }

    /// <summary>
    /// Relocates an existing mirror entry from one relative path to another. If
    /// <paramref name="fromRelativePath"/> no longer exists but <paramref name="toRelativePath"/>
    /// already does, a prior run already completed this exact relocation before being
    /// interrupted between the mirror rename and its manifest commit (see design.md's
    /// "Crash between a move's mirror relocation and its manifest commit") - the relocation
    /// is treated as already satisfied instead of failing every retry. If neither path
    /// exists, this is a genuine failure and <see cref="File.Move(string, string, bool)"/>
    /// still throws as before.
    /// </summary>
    public void MoveMirrorEntry(string fromRelativePath, string toRelativePath)
    {
        var fromPath = Path.Combine(_mirrorRoot, fromRelativePath);
        var toPath = Path.Combine(_mirrorRoot, toRelativePath);

        if (!File.Exists(fromPath) && File.Exists(toPath))
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(toPath)!);

        // fromPath can itself be read-only (a mirror entry placed via hardlink), and so can an
        // existing toPath (an interrupted prior run's leftover destination) - both would make
        // File.Move(overwrite: true) throw UnauthorizedAccessException. Capture the source's
        // own attribute before clearing the destination: clearing toPath's read-only can also
        // clear fromPath's, when they happen to be hardlinks to the same blob (the likeliest
        // occupied-destination case, since it arises from an interrupted run that already
        // performed part of this same relocation) - NTFS shares FileAttributes.ReadOnly per
        // underlying file, not per hardlink name (see design.md).
        var sourceWasReadOnly = File.Exists(fromPath) && File.GetAttributes(fromPath).HasFlag(FileAttributes.ReadOnly);
        ClearReadOnlyIfPresent(toPath);
        File.Move(fromPath, toPath, overwrite: true);

        // Restore the relocated entry's own attribute rather than unconditionally re-asserting
        // it, so a copy-fallback (writable) entry is not silently upgraded to read-only just
        // because it happened to overwrite a previously read-only destination.
        if (sourceWasReadOnly)
        {
            EnsureReadOnlyIfPresent(toPath);
        }

        // Residual, accepted limitation: this cannot re-protect the blob of the content this
        // move *displaces* at an occupied destination - the clear above can leave that
        // content's blob, and any mirror path still deduplicated against it, writable, because
        // this method has no hash identifying the displaced content (the destination is by
        // definition an "Add" path with no manifest row to supply one from). Bounded: it only
        // arises from an occupied move destination, which itself only arises from an
        // interrupted run, and any later run that places, changes, or removes a path
        // referencing that blob re-asserts its protection through the existing
        // EnsureReadOnlyIfPresent calls elsewhere in this class.
    }

    public void RemoveFromMirror(string mirrorRelativePath, string hash)
    {
        var mirrorPath = Path.Combine(_mirrorRoot, mirrorRelativePath);
        if (File.Exists(mirrorPath))
        {
            // File.Delete throws UnauthorizedAccessException against a read-only target
            // (verified empirically) - a mirror entry placed via hardlink is marked
            // read-only, so this clears it first rather than leaving every hardlinked
            // file's deletion failing.
            ClearReadOnlyIfPresent(mirrorPath);
            File.Delete(mirrorPath);

            // The clear above affects every hardlink to this content, not just mirrorPath -
            // including the content's own canonical blob file, and any other mirror path
            // still deduplicated against it (NTFS shares FileAttributes.ReadOnly per
            // underlying file, not per hardlink name - see design.md). Restoring it via the
            // blob's own name re-protects all of those survivors uniformly. Safe to attempt
            // unconditionally: a no-op if this entry was a copy-fallback placement, or if the
            // blob has since been pruned.
            EnsureReadOnlyIfPresent(BlobPath(hash));
        }
    }

    public void DeleteContent(string hash)
    {
        var blobPath = BlobPath(hash);
        if (File.Exists(blobPath))
        {
            // A blob can now be read-only (see PlaceAtMirrorPath/RemoveFromMirror), so
            // File.Delete would otherwise throw UnauthorizedAccessException. No restore is
            // needed afterward - PruneService only calls this for content it has already
            // confirmed is unreferenced by any live mirror path.
            ClearReadOnlyIfPresent(blobPath);
            File.Delete(blobPath);
        }
    }

    public void CleanupOrphanedTemp()
    {
        if (!Directory.Exists(_tempRoot))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(_tempRoot))
        {
            TryDelete(file);
        }
    }

    public void ExtractTo(string hash, string destinationAbsolutePath, Action<long>? onBytesCopied = null)
    {
        var blobPath = BlobPath(hash);
        if (!File.Exists(blobPath))
        {
            throw new FileNotFoundException($"No stored content for hash '{hash}'.", blobPath);
        }

        var directory = Path.GetDirectoryName(destinationAbsolutePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // CopyWithProgress opens its destination with FileMode.CreateNew, so an existing
        // destination file is deleted first to preserve this method's previous
        // File.Copy(overwrite: true) semantics (already gated by SnapshotHistoryService's
        // GuardDestination, which only lets execution reach here for an existing destination
        // when the caller explicitly authorized overwriting it).
        if (File.Exists(destinationAbsolutePath))
        {
            File.Delete(destinationAbsolutePath);
        }

        CopyWithProgress(blobPath, destinationAbsolutePath, onBytesCopied);
    }

    public void RemoveExtractedFile(string absolutePath)
    {
        if (File.Exists(absolutePath))
        {
            File.Delete(absolutePath);
        }
    }

    public bool IsWithinMirror(string absolutePath)
    {
        var resolvedMirrorRoot = Path.GetFullPath(_mirrorRoot);
        var mirrorRootWithSeparator = resolvedMirrorRoot.EndsWith(Path.DirectorySeparatorChar)
            ? resolvedMirrorRoot
            : resolvedMirrorRoot + Path.DirectorySeparatorChar;

        var resolvedPath = Path.GetFullPath(absolutePath);
        return resolvedPath.Equals(resolvedMirrorRoot, StringComparison.OrdinalIgnoreCase)
            || resolvedPath.StartsWith(mirrorRootWithSeparator, StringComparison.OrdinalIgnoreCase);
    }

    public bool TargetExists(string absolutePath) => File.Exists(absolutePath);

    public IReadOnlySet<string> ListAllStoredHashes()
    {
        if (!Directory.Exists(_versionsRoot))
        {
            return new HashSet<string>();
        }

        return Directory.EnumerateFiles(_versionsRoot, "*", SearchOption.AllDirectories)
            .Select(Path.GetFileName)
            .Where(name => !string.IsNullOrEmpty(name))
            .Select(name => name!)
            .ToHashSet();
    }

    private string BlobPath(string hash) => Path.Combine(_versionsRoot, hash[..2], hash);

    /// <summary>
    /// Test-only seam: bypasses the real hardlink probe so the copy-fallback code path
    /// can be exercised deterministically without needing a non-hardlink-capable volume.
    /// </summary>
    internal void ForceHardlinkSupportForTesting(bool supported) => _supportsHardlinks = supported;

    /// <summary>
    /// Test-only seam: causes the single next hardlink attempt inside
    /// <see cref="PlaceAtMirrorPath"/> to fail as if the volume rejected it (e.g. NTFS's
    /// per-file hard-link limit), so the per-placement copy-fallback path can be exercised
    /// deterministically without actually creating enough real hardlinks to exhaust a blob's
    /// link count. Resets itself after triggering exactly one failure.
    /// </summary>
    internal void ForceNextHardlinkFailureForTesting() => _forceNextHardlinkFailureForTesting = true;

    private static void TryDelete(string path)
    {
        try
        {
            // A staged file can be orphaned mid-way through PlaceAtMirrorPath (e.g. a crash
            // after it's marked read-only but before the final move) - clear the attribute
            // first so cleanup can actually remove it instead of silently leaving it behind
            // forever.
            ClearReadOnlyIfPresent(path);
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
