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

    public FileSystemContentStore(string targetRoot, IHasher hasher)
    {
        _mirrorRoot = targetRoot;
        _versionsRoot = Path.Combine(targetRoot, ".vara", "versions");
        _tempRoot = Path.Combine(targetRoot, ".vara", "tmp");
        _hasher = hasher;

        Directory.CreateDirectory(_mirrorRoot);
        Directory.CreateDirectory(_versionsRoot);
        Directory.CreateDirectory(_tempRoot);
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

    public void PlaceAtMirrorPath(string hash, string mirrorRelativePath, Action<long>? onBytesCopied = null)
    {
        var blobPath = BlobPath(hash);
        if (!File.Exists(blobPath))
        {
            throw new FileNotFoundException($"No stored content for hash '{hash}'.", blobPath);
        }

        var mirrorPath = Path.Combine(_mirrorRoot, mirrorRelativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(mirrorPath)!);

        var stagingPath = Path.Combine(_tempRoot, Guid.NewGuid().ToString("N"));
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
            }
            catch (IOException)
            {
                // Most notably NTFS's 1024-hard-link-per-file cap: once a blob is already
                // referenced by the maximum number of hard links, creating another one
                // fails. Falling back to a real copy for just this placement keeps the run
                // succeeding instead of permanently failing every mirror path beyond the cap.
                CopyWithProgress(blobPath, stagingPath, onBytesCopied);
            }
        }
        else
        {
            CopyWithProgress(blobPath, stagingPath, onBytesCopied);
        }

        // Atomic swap: rename the staged file into place, replacing any existing mirror
        // entry in one filesystem operation - never an in-place overwrite.
        File.Move(stagingPath, mirrorPath, overwrite: true);
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
        File.Move(fromPath, toPath, overwrite: true);
    }

    public void RemoveFromMirror(string mirrorRelativePath)
    {
        var mirrorPath = Path.Combine(_mirrorRoot, mirrorRelativePath);
        if (File.Exists(mirrorPath))
        {
            File.Delete(mirrorPath);
        }
    }

    public void DeleteContent(string hash)
    {
        var blobPath = BlobPath(hash);
        if (File.Exists(blobPath))
        {
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
