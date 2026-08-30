using Vara.Core.Abstractions;
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
    private readonly string _mirrorRoot;
    private readonly string _versionsRoot;
    private readonly string _tempRoot;
    private readonly IHasher _hasher;
    private bool? _supportsHardlinks;

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

    public (string Hash, long Size) StoreFromStream(Stream content)
    {
        var tempPath = Path.Combine(_tempRoot, Guid.NewGuid().ToString("N"));
        long size;

        using (var tempFile = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write))
        {
            content.CopyTo(tempFile);
            size = tempFile.Length;
        }

        // Re-reading the just-written temp file to hash it is a deliberate simplification:
        // it keeps IHasher's simple "hash a stream" contract, at the cost of a second local
        // (page-cache-warm) read rather than a single combined copy+hash pass. Purely an
        // internal performance detail; can be optimized later without any interface change.
        string hash;
        using (var readBack = File.OpenRead(tempPath))
        {
            hash = _hasher.ComputeHash(readBack);
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

    public void PlaceAtMirrorPath(string hash, string mirrorRelativePath)
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
            Kernel32.CreateHardLink(stagingPath, blobPath);
        }
        else
        {
            File.Copy(blobPath, stagingPath);
        }

        // Atomic swap: rename the staged file into place, replacing any existing mirror
        // entry in one filesystem operation - never an in-place overwrite.
        File.Move(stagingPath, mirrorPath, overwrite: true);
    }

    public void MoveMirrorEntry(string fromRelativePath, string toRelativePath)
    {
        var fromPath = Path.Combine(_mirrorRoot, fromRelativePath);
        var toPath = Path.Combine(_mirrorRoot, toRelativePath);
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

    public void ExtractTo(string hash, string destinationAbsolutePath)
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

        File.Copy(blobPath, destinationAbsolutePath, overwrite: true);
    }

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
