using Vara.Cli.Commands;
using Vara.Core.Abstractions;
using Vara.Core.Snapshots;
using Xunit;

namespace Vara.Cli.Tests.Commands;

public class DirectoryArgumentResolverTests
{
    /// <summary>Minimal <see cref="ISnapshotRepository"/> stub exposing only the two members
    /// <see cref="DirectoryArgumentResolver"/> actually calls (<see cref="GetCurrentState"/>,
    /// <see cref="GetTombstones"/>) plus <see cref="GetFileHistory"/> (used by
    /// <see cref="SnapshotPathResolver"/>'s own literal-match step); every other member is
    /// unused by this resolver and throws if accidentally called.</summary>
    private sealed class StubRepository(IReadOnlyDictionary<string, CurrentFileState> current, IReadOnlyList<FileVersionRecord>? tombstones = null) : ISnapshotRepository
    {
        public void Dispose() { }
        public void ReconcileIncompleteSnapshots() => throw new NotSupportedException();
        public long BeginSnapshot(DateTimeOffset startedAt) => throw new NotSupportedException();
        public void RecordFileVersion(long snapshotId, string relativePath, string? previousRelativePath, string contentHash, long size, DateTimeOffset sourceModifiedAt, FileChangeKind changeKind, DateTimeOffset recordedAt, string? quickHash = null, int? quickHashScheme = null) => throw new NotSupportedException();
        public void CompleteSnapshot(long snapshotId, DateTimeOffset completedAt, SnapshotStats stats) => throw new NotSupportedException();
        public void FailSnapshot(long snapshotId, DateTimeOffset failedAt, SnapshotStats stats) => throw new NotSupportedException();
        public IManifestBatch BeginManifestBatch() => throw new NotSupportedException();
        public IReadOnlyDictionary<string, CurrentFileState> GetCurrentState() => current;
        public IReadOnlyDictionary<string, CurrentFileState> GetStateAsOf(DateTimeOffset asOf) => throw new NotSupportedException();
        public IReadOnlyList<FileVersionRecord> GetTombstones(DateTimeOffset? asOf) => tombstones ?? [];
        public IReadOnlyDictionary<string, string> GetMoveOrigins() => new Dictionary<string, string>();
        public IReadOnlyList<Snapshot> ListSnapshots() => throw new NotSupportedException();
        public Snapshot? GetLastCompletedSnapshot() => throw new NotSupportedException();
        public IReadOnlyList<FileVersionRecord> GetFileHistory(string relativePath) => [];
        public FileVersionRecord? FindVersionAsOf(string relativePath, DateTimeOffset asOf) => throw new NotSupportedException();
        public void DeleteSnapshot(long snapshotId) => throw new NotSupportedException();
        public int PruneSnapshots(IReadOnlyList<long> snapshotIds) => throw new NotSupportedException();
        public IReadOnlySet<string> GetAllReferencedContentHashes() => throw new NotSupportedException();
    }

    [Fact]
    public void Resolves_an_absolute_source_path_to_its_mirror_relative_form()
    {
        var current = new Dictionary<string, CurrentFileState>(StringComparer.OrdinalIgnoreCase)
        {
            [@"C\Users\john\Projects\src\main.py"] = new(@"C\Users\john\Projects\src\main.py", "hash", 10, DateTimeOffset.UnixEpoch),
        };
        var repository = new StubRepository(current);

        var resolved = DirectoryArgumentResolver.Resolve(@"D:\Backups\Profile1", @"C:\Users\john\Projects\src", repository, IsWithinMirror);

        Assert.Equal(@"C\Users\john\Projects\src", resolved);
    }

    [Fact]
    public void Falls_back_to_the_raw_input_when_no_interpretation_resolves()
    {
        var repository = new StubRepository(new Dictionary<string, CurrentFileState>());

        var resolved = DirectoryArgumentResolver.Resolve(@"D:\Backups\Profile1", "never-tracked-dir", repository, IsWithinMirror);

        Assert.Equal("never-tracked-dir", resolved);
    }

    [Fact]
    public void The_mirror_root_itself_always_resolves()
    {
        var repository = new StubRepository(new Dictionary<string, CurrentFileState>());

        var resolved = DirectoryArgumentResolver.Resolve(@"D:\Backups\Profile1", ".", repository, IsWithinMirror);

        Assert.Equal(".", resolved);
    }

    /// <summary>Plain lexical containment check standing in for the reparse-aware
    /// <c>IContentStore.IsWithinMirror</c> a real caller passes - equivalent for these tests
    /// since none of them exercise a symlink/junction.</summary>
    private static bool IsWithinMirror(string absolutePath) =>
        absolutePath.Equals(@"D:\Backups\Profile1", StringComparison.OrdinalIgnoreCase)
        || absolutePath.StartsWith(@"D:\Backups\Profile1\", StringComparison.OrdinalIgnoreCase);
}
