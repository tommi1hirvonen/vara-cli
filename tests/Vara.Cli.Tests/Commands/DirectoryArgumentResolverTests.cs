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
        public void RecordFileVersion(long snapshotId, string relativePath, string? previousRelativePath, string? contentHash, long size, DateTimeOffset sourceModifiedAt, FileChangeKind changeKind, DateTimeOffset recordedAt, string? quickHash = null, int? quickHashScheme = null, string? linkTarget = null) => throw new NotSupportedException();
        public void CompleteSnapshot(long snapshotId, DateTimeOffset completedAt, SnapshotStats stats) => throw new NotSupportedException();
        public void FailSnapshot(long snapshotId, DateTimeOffset failedAt, SnapshotStats stats) => throw new NotSupportedException();
        public void CancelSnapshot(long snapshotId, DateTimeOffset cancelledAt, SnapshotStats stats) => throw new NotSupportedException();
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
        public IReadOnlyList<string> GetPathsForContentHash(string hash) => throw new NotSupportedException();
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

        Assert.Equal(@"C\Users\john\Projects\src", resolved.Path);
        Assert.False(resolved.FellBackToMirrorRoot);
    }

    [Fact]
    public void Falls_back_to_the_raw_input_when_no_interpretation_resolves()
    {
        var repository = new StubRepository(new Dictionary<string, CurrentFileState>());

        // "never-tracked-dir" is a non-root, non-blank argument - unaffected by the `.`/blank
        // fallback handling, per the "Non-root directory argument from within a source is
        // unaffected" scenario.
        var resolved = DirectoryArgumentResolver.Resolve(
            @"D:\Backups\Profile1", "never-tracked-dir", repository, IsWithinMirror, currentDirectory: @"C:\Users\john\Elsewhere");

        Assert.Equal("never-tracked-dir", resolved.Path);
        Assert.False(resolved.FellBackToMirrorRoot);
    }

    [Fact]
    public void Dot_from_a_working_directory_inside_a_source_resolves_to_the_source_mapped_directory()
    {
        // The cwd (C:\Users\john\Projects\src) is outside the mirror, but maps to a
        // mirror-relative directory that has recorded history - the "Browsing or recursively
        // restoring the current directory from within a source" scenario.
        var current = new Dictionary<string, CurrentFileState>(StringComparer.OrdinalIgnoreCase)
        {
            [@"C\Users\john\Projects\src\main.py"] = new(@"C\Users\john\Projects\src\main.py", "hash", 10, DateTimeOffset.UnixEpoch),
        };
        var repository = new StubRepository(current);

        var resolved = DirectoryArgumentResolver.Resolve(
            @"D:\Backups\Profile1", ".", repository, IsWithinMirror, currentDirectory: @"C:\Users\john\Projects\src");

        Assert.Equal(@"C\Users\john\Projects\src", resolved.Path);
        Assert.False(resolved.FellBackToMirrorRoot);
    }

    [Fact]
    public void The_mirror_root_itself_always_resolves()
    {
        var repository = new StubRepository(new Dictionary<string, CurrentFileState>());

        // The cwd's source-mapped location has no recorded history (the repository is empty),
        // so this falls back to the mirror root and signals that fallback - the "Current
        // directory outside the mirror has no recorded history" scenario.
        var resolved = DirectoryArgumentResolver.Resolve(
            @"D:\Backups\Profile1", ".", repository, IsWithinMirror, currentDirectory: @"C:\Users\john\Projects\src");

        Assert.Equal(".", resolved.Path);
        Assert.True(resolved.FellBackToMirrorRoot);
    }

    [Fact]
    public void Dot_from_a_working_directory_inside_the_mirror_resolves_without_falling_back()
    {
        var repository = new StubRepository(new Dictionary<string, CurrentFileState>());

        // The cwd is inside the mirror, so the mirror root is the directly-requested directory,
        // not a fallback - unaffected by the `.`/blank-while-outside-the-mirror special case.
        // The cwd-relative candidate strips the mirror root from the cwd itself, producing an
        // empty mirror-relative path - equivalent to "." (both normalize to the root prefix).
        var resolved = DirectoryArgumentResolver.Resolve(
            @"D:\Backups\Profile1", ".", repository, IsWithinMirror, currentDirectory: @"D:\Backups\Profile1");

        Assert.Equal(string.Empty, resolved.Path);
        Assert.False(resolved.FellBackToMirrorRoot);
    }

    /// <summary>Plain lexical containment check standing in for the reparse-aware
    /// <c>IContentStore.IsWithinMirror</c> a real caller passes - equivalent for these tests
    /// since none of them exercise a symlink/junction.</summary>
    private static bool IsWithinMirror(string absolutePath) =>
        absolutePath.Equals(@"D:\Backups\Profile1", StringComparison.OrdinalIgnoreCase)
        || absolutePath.StartsWith(@"D:\Backups\Profile1\", StringComparison.OrdinalIgnoreCase);
}
