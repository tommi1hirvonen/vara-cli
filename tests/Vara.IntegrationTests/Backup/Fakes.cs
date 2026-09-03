using System.Security.Cryptography;
using Vara.Core.Abstractions;
using Vara.Core.Configuration;

namespace Vara.IntegrationTests.Backup;

/// <summary>Deterministic content-based fake hasher (SHA-256, hex) - no production dependency, just distinguishes content for tests.
/// Copied from <c>Vara.Application.Tests/Backup/Fakes.cs</c> - see design.md's decision to duplicate
/// small, drift-risk-free fakes into this project rather than reference another test project.</summary>
internal sealed class FakeHasher : IHasher
{
    public string ComputeHash(Stream content)
    {
        using var buffer = new MemoryStream();
        content.CopyTo(buffer);
        return Convert.ToHexString(SHA256.HashData(buffer.ToArray()));
    }
}

/// <summary>Fully in-memory content store standing in for <see cref="Vara.Infrastructure.Storage.FileSystemContentStore"/>,
/// used only for <c>BackupPipeline</c> tests in this project - <c>PruneService</c> and
/// <c>SnapshotHistoryService</c> tests use the real <see cref="Vara.Infrastructure.Storage.FileSystemContentStore"/>
/// instead (see design.md's Decisions). Thread-safe, since production execution parallelizes Add/Change operations.</summary>
internal sealed class FakeContentStore : IContentStore
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte[]> _blobs = new();
    public readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> Mirror = new(StringComparer.OrdinalIgnoreCase);
    private readonly FakeHasher _hasher = new();
    private readonly HashSet<string> _existingTargets = new(StringComparer.OrdinalIgnoreCase);

    public bool SupportsHardlinks { get; private set; }
    public bool ProbeHardlinkSupport() => SupportsHardlinks = true;

    public (string Hash, long Size) StoreFromStream(Stream content, Action<long>? onBytesWritten = null)
    {
        using var buffer = new MemoryStream();
        content.CopyTo(buffer);
        var bytes = buffer.ToArray();
        var hash = _hasher.ComputeHash(new MemoryStream(bytes));
        _blobs.TryAdd(hash, bytes);
        if (bytes.LongLength > 0)
        {
            onBytesWritten?.Invoke(bytes.LongLength);
        }

        return (hash, bytes.LongLength);
    }

    public bool HasContent(string hash) => _blobs.ContainsKey(hash);

    public void PlaceAtMirrorPath(string hash, string mirrorRelativePath, Action<long>? onBytesCopied = null)
    {
        Mirror[mirrorRelativePath] = hash;
    }

    public void MoveMirrorEntry(string fromRelativePath, string toRelativePath)
    {
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

    public void RemoveFromMirror(string mirrorRelativePath) => Mirror.TryRemove(mirrorRelativePath, out _);

    public void DeleteContent(string hash) => _blobs.TryRemove(hash, out _);
    public void CleanupOrphanedTemp() { }
    public IReadOnlySet<string> ListAllStoredHashes() => _blobs.Keys.ToHashSet();

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
        const int chunkSize = 8192;
        using var destination = new FileStream(destinationAbsolutePath, FileMode.Create, FileAccess.Write);
        for (var offset = 0; offset < bytes.Length; offset += chunkSize)
        {
            var length = Math.Min(chunkSize, bytes.Length - offset);
            destination.Write(bytes, offset, length);
            onBytesCopied?.Invoke(length);
        }
    }

    public bool IsWithinMirror(string absolutePath) => false;
    public bool TargetExists(string absolutePath) => _existingTargets.Contains(absolutePath);
}

/// <summary>Fake scanner returning a pre-set, test-controlled list of entries (and, optionally, scan failures) regardless of the sources argument.</summary>
internal sealed class FakeFileSystemScanner(IReadOnlyList<ScannedEntry> entries, IReadOnlyList<ScanFailure>? failures = null) : IFileSystemScanner
{
    public ScanResult Scan(IReadOnlyList<Source> sources) => new(entries, failures ?? []);
}

/// <summary>Fake run lock whose acquisition outcome is test-controlled.</summary>
internal sealed class FakeRunLock(bool acquirable = true) : IRunLock
{
    public void Dispose() { }
    public bool TryAcquire(string profileName, string targetRoot) => acquirable;
}
