using System.Collections.Concurrent;
using System.Security.Cryptography;
using Vara.Core.Abstractions;
using Vara.Core.Configuration;

namespace Vara.TestSupport;

/// <summary>
/// Deterministic content-based fake hasher (SHA-256, hex) - no production dependency, just distinguishes content for tests.
/// </summary>
public sealed class FakeHasher : IHasher
{
    public string ComputeHash(Stream content)
    {
        using var buffer = new MemoryStream();
        content.CopyTo(buffer);
        return Convert.ToHexString(SHA256.HashData(buffer.ToArray()));
    }
}

/// <summary>A hasher for tests whose code path must never hash content.</summary>
public sealed class UnusedHasher : IHasher
{
    public string ComputeHash(Stream content) => throw new NotSupportedException();
}

/// <summary>
/// Fully in-memory content store standing in for <c>FileSystemContentStore</c>.
/// Thread-safe, since production execution parallelizes Add/Change operations.
/// </summary>
public sealed class FakeContentStore : IContentStore
{
    // Mirrors FileSystemContentStore's own chunked reads closely enough to exercise
    // "many small progress reports for one large file" in tests, without needing real
    // disk I/O or the production TeeStream plumbing.
    private const int SimulatedChunkSize = 8192;

    private readonly ConcurrentDictionary<string, byte[]> _blobs = new();
    public readonly ConcurrentDictionary<string, string> Mirror = new(StringComparer.OrdinalIgnoreCase);
    private readonly FakeHasher _hasher = new();
    private readonly HashSet<string> _existingTargets = new(StringComparer.OrdinalIgnoreCase);
    private bool? _forcedSupportsHardlinks;

    /// <summary>
    /// Test seam: when set, <see cref="PlaceAtMirrorPath"/> throws this instead of performing the placement -
    /// only for a call whose <c>mirrorRelativePath</c> equals <see cref="ThrowOnPlaceForPath"/> (or for every
    /// call, when that is left unset), so a test can fail one specific placement while others in the same
    /// run proceed normally.
    /// </summary>
    public Exception? ThrowOnPlace { get; set; }

    /// <summary>
    /// Test seam: restricts <see cref="ThrowOnPlace"/> to only the placement whose <c>mirrorRelativePath</c>
    /// matches this value. Unset (<see langword="null"/>) means every placement throws.
    /// </summary>
    public string? ThrowOnPlaceForPath { get; set; }

    /// <summary>Test seam: when set, <see cref="MoveMirrorEntry"/> throws this instead of performing the move.</summary>
    public Exception? ThrowOnMove { get; set; }

    /// <summary>Test seam: when set, <see cref="RemoveFromMirror"/> throws this instead of performing the removal.</summary>
    public Exception? ThrowOnRemove { get; set; }

    /// <summary>Test seam: destination paths for which <see cref="IsWithinMirror"/> should report containment.</summary>
    public HashSet<string> MirrorPaths { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Test seam: records the <c>previousContentHash</c> argument passed to each <see cref="PlaceAtMirrorPath"/> call,
    /// keyed by mirror path - lets a test assert BackupExecutor threads the previous content's hash through correctly
    /// for a Changed operation.
    /// </summary>
    public ConcurrentDictionary<string, string?> PreviousContentHashesSeen { get; } =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Test seam: records the <c>hash</c> argument passed to each <see cref="RemoveFromMirror"/> call,
    /// keyed by mirror path - lets a test assert BackupExecutor threads the deleted file's known hash through correctly.
    /// </summary>
    public ConcurrentDictionary<string, string> RemoveFromMirrorHashesSeen { get; } =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Test seam: marks <paramref name="absolutePath"/> as already existing, so <see cref="TargetExists"/> reports it.</summary>
    public void SeedExistingTarget(string absolutePath) => _existingTargets.Add(absolutePath);

    /// <summary>
    /// Test seam: simulates a target filesystem without hardlink support, so
    /// <see cref="PlaceAtMirrorPath"/> re-streams every placed blob's bytes (with
    /// progress) instead of being a free no-op - mirroring FileSystemContentStore's real
    /// fallback-copy behavior. Overrides whatever <see cref="ProbeHardlinkSupport"/>
    /// would otherwise report, including when called after it (e.g. by a pipeline run).
    /// </summary>
    public void ForceHardlinkSupportForTesting(bool supported) => SupportsHardlinks = (_forcedSupportsHardlinks = supported).Value;

    // Defaults to true (matching this fake's original always-true behavior) so tests that
    // exercise BackupExecutor directly - never calling ProbeHardlinkSupport, which only
    // BackupPipeline.Run does - don't accidentally take the fallback-copy simulation path.
    public bool SupportsHardlinks { get; private set; } = true;
    public bool ProbeHardlinkSupport() => SupportsHardlinks = _forcedSupportsHardlinks ?? true;

    public (string Hash, long Size) StoreFromStream(Stream content, Action<long>? onBytesWritten = null)
    {
        using var buffer = new MemoryStream();
        content.CopyTo(buffer);
        var bytes = buffer.ToArray();
        var hash = _hasher.ComputeHash(new MemoryStream(bytes));
        _blobs.TryAdd(hash, bytes);
        ReportInChunks(bytes.LongLength, onBytesWritten);
        return (hash, bytes.LongLength);
    }

    public bool HasContent(string hash) => _blobs.ContainsKey(hash);

    /// <summary>
    /// Test seam: when set, <see cref="OpenRead"/> throws this instead of returning a stream for a call whose
    /// <c>hash</c> equals this value - lets a test simulate one side of a diff failing to open after the other side
    /// already opened successfully, to verify the successfully-opened stream is disposed rather than leaked.
    /// </summary>
    public string? ThrowOnOpenReadForHash { get; set; }

    /// <summary>
    /// Test seam: records each hash whose returned stream has been disposed, so a test can assert a stream
    /// opened for one side of a diff was released even though the caller never got to read from it.
    /// </summary>
    public ConcurrentBag<string> DisposedHashes { get; } = new();

    public Stream OpenRead(string hash)
    {
        if (ThrowOnOpenReadForHash is not null && string.Equals(ThrowOnOpenReadForHash, hash, StringComparison.Ordinal))
        {
            throw new FileNotFoundException($"No stored content for hash '{hash}'.");
        }

        if (!_blobs.TryGetValue(hash, out var bytes))
        {
            throw new FileNotFoundException($"No stored content for hash '{hash}'.");
        }

        return new DisposeTrackingStream(bytes, hash, DisposedHashes);
    }

    /// <summary>
    /// Wraps a read-only <see cref="MemoryStream"/> so <see cref="FakeContentStore.OpenRead"/> can report when its
    /// caller disposes it, without changing any other observable behavior.
    /// </summary>
    private sealed class DisposeTrackingStream(
        byte[] bytes,
        string hash,
        ConcurrentBag<string> disposedHashes) : MemoryStream(bytes, writable: false)
    {
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                disposedHashes.Add(hash);
            }

            base.Dispose(disposing);
        }
    }

    public Action<long>? LastOnBytesCopiedPassedToPlace { get; private set; }

    public void PlaceAtMirrorPath(
        string hash,
        string mirrorRelativePath,
        Action<long>? onBytesCopied = null,
        string? previousContentHash = null)
    {
        LastOnBytesCopiedPassedToPlace = onBytesCopied;
        if (ThrowOnPlace is not null &&
            (ThrowOnPlaceForPath is null ||
             string.Equals(ThrowOnPlaceForPath, mirrorRelativePath, StringComparison.OrdinalIgnoreCase)))
        {
            throw ThrowOnPlace;
        }

        Mirror[mirrorRelativePath] = hash;
        PreviousContentHashesSeen[mirrorRelativePath] = previousContentHash;
        if (!SupportsHardlinks && _blobs.TryGetValue(hash, out var bytes))
        {
            // Every placement falls back to a streamed copy on a target without
            // hardlink support, just like the real FileSystemContentStore.
            ReportInChunks(bytes.LongLength, onBytesCopied);
        }
    }

    private static void ReportInChunks(long totalBytes, Action<long>? onBytesReported)
    {
        if (onBytesReported is null)
        {
            return;
        }

        var remaining = totalBytes;
        while (remaining > 0)
        {
            var chunk = Math.Min(SimulatedChunkSize, remaining);
            onBytesReported(chunk);
            remaining -= chunk;
        }
    }

    public void MoveMirrorEntry(string fromRelativePath, string toRelativePath)
    {
        if (ThrowOnMove is not null)
        {
            throw ThrowOnMove;
        }

        if (Mirror.TryRemove(fromRelativePath, out var hash))
        {
            Mirror[toRelativePath] = hash;
            return;
        }

        if (Mirror.ContainsKey(toRelativePath))
        {
            // Mirrors FileSystemContentStore.MoveMirrorEntry's crash-recovery behavior:
            // the source is already gone but the destination already holds the expected
            // content, meaning a prior interrupted run already completed this relocation.
            return;
        }

        throw new FileNotFoundException($"No mirror entry at '{fromRelativePath}' or '{toRelativePath}'.");
    }

    public void RemoveFromMirror(string mirrorRelativePath, string hash)
    {
        if (ThrowOnRemove is not null)
        {
            throw ThrowOnRemove;
        }

        RemoveFromMirrorHashesSeen[mirrorRelativePath] = hash;
        Mirror.TryRemove(mirrorRelativePath, out _);
    }

    public void DeleteContent(string hash) => _blobs.TryRemove(hash, out _);
    public void CleanupOrphanedTemp() { }
    public IReadOnlySet<string> ListAllStoredHashes() => _blobs.Keys.ToHashSet();

    /// <summary>Test seam: overwrites an already-stored blob's bytes with different
    /// content, without changing the hash key it's filed under - simulates target-side
    /// bit rot/corruption for integrity-check tests, where a blob's on-disk bytes no
    /// longer match the hash the manifest recorded for it.</summary>
    public void CorruptBlob(string hash, string corruptedContent) => _blobs[hash] = System.Text.Encoding.UTF8.GetBytes(corruptedContent);

    public void ExtractTo(string hash, string destinationAbsolutePath, Action<long>? onBytesCopied = null)
    {
        if (!_blobs.TryGetValue(hash, out var bytes))
        {
            throw new FileNotFoundException($"No stored content for hash '{hash}'.");
        }

        var directory = Path.GetDirectoryName(destinationAbsolutePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // Copies in fixed-size chunks (rather than one File.WriteAllBytes call) so tests
        // can exercise onBytesCopied's incremental-progress behavior against a fake store.
        using var destination = new FileStream(destinationAbsolutePath, FileMode.Create, FileAccess.Write);
        for (var offset = 0; offset < bytes.Length; offset += SimulatedChunkSize)
        {
            var length = Math.Min(SimulatedChunkSize, bytes.Length - offset);
            destination.Write(bytes, offset, length);
            onBytesCopied?.Invoke(length);
        }
    }

    public void RemoveExtractedFile(string absolutePath)
    {
        _existingTargets.Remove(absolutePath);
        if (File.Exists(absolutePath))
        {
            File.Delete(absolutePath);
        }
    }

    public bool IsWithinMirror(string absolutePath) => MirrorPaths.Contains(absolutePath);
    public bool TargetExists(string absolutePath) => _existingTargets.Contains(absolutePath);
}

/// <summary>
/// Fake scanner returning a pre-set, test-controlled list of entries (and, optionally, scan failures)
/// regardless of the sources argument.
/// </summary>
public sealed class FakeFileSystemScanner(
    IReadOnlyList<ScanItem>? entries = null,
    IReadOnlyList<ScanFailure>? failures = null) : IFileSystemScanner
{
    public ScanResult Scan(IReadOnlyList<Source> sources) => new(entries ?? [], failures ?? []);
}

/// <summary>Fake run lock whose acquisition outcome is test-controlled.</summary>
public sealed class FakeRunLock(bool acquirable = true) : IRunLock
{
    public bool AcquireAttempted { get; private set; }
    public void Dispose() { }
    public bool TryAcquire(string profileName, string targetRoot)
    {
        AcquireAttempted = true;
        return acquirable;
    }
}

/// <summary>An <see cref="IProfileConfigLoader"/> that yields a fixed list of profiles.</summary>
public sealed class StubProfileConfigLoader(IReadOnlyList<Profile> profiles) : IProfileConfigLoader
{
    public string DefaultConfigPath => @"C:\fake\.vara\profiles.yml";
    public IReadOnlyList<Profile> LoadProfiles(string configPath) => profiles;
}

/// <summary>An <see cref="IProfileConfigLoader"/> that always yields the one given profile.</summary>
public sealed class SingleProfileConfigLoader(Profile profile) : IProfileConfigLoader
{
    public IReadOnlyList<Profile> LoadProfiles(string configPath) => [profile];
    public string DefaultConfigPath => "unused";
}

/// <summary>A loader for tests whose code path must never reach profile resolution.</summary>
public sealed class UnusedProfileConfigLoader : IProfileConfigLoader
{
    public IReadOnlyList<Profile> LoadProfiles(string configPath) => throw new NotSupportedException();
    public string DefaultConfigPath => throw new NotSupportedException();
}
