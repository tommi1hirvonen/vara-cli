using Microsoft.Data.Sqlite;
using Vara.Application.History;
using Vara.Core.Snapshots;
using Vara.Infrastructure.Hashing;
using Vara.Infrastructure.Snapshots;
using Vara.Infrastructure.Storage;
using Vara.IntegrationTests.TestSupport;
using Xunit;

namespace Vara.IntegrationTests.History;

/// <summary>
/// Runs <see cref="SnapshotHistoryService"/> against the real <see cref="SqliteSnapshotRepository"/>
/// AND the real <see cref="FileSystemContentStore"/>, per design.md's decision: restore's
/// mirror-containment guard (<c>IsWithinMirror</c>) and overwrite check (<c>TargetExists</c>)
/// rely on the content store's real path-resolution logic, which the fake re-implements
/// independently via separate tracked sets (<c>MirrorPaths</c>, <c>SeedExistingTarget</c>).
/// </summary>
public class SnapshotHistoryServiceRealPortsTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"vara-inttest-{Guid.NewGuid():N}.db");
    private readonly string _targetRoot = Path.Combine(Path.GetTempPath(), $"vara-inttest-{Guid.NewGuid():N}");
    private SqliteSnapshotRepository? _repository;
    private FileSystemContentStore? _contentStore;

    private SqliteSnapshotRepository Repository => _repository ??= new SqliteSnapshotRepository(_dbPath);
    private FileSystemContentStore ContentStore => _contentStore ??= new FileSystemContentStore(_targetRoot, new XxHash128Hasher());

    public void Dispose()
    {
        _repository?.Dispose();
        SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath))
        {
            File.Delete(_dbPath);
        }

        if (Directory.Exists(_targetRoot))
        {
            DirectoryCleanup.ClearReadOnlyAndDelete(_targetRoot);
        }
    }

    [Fact]
    public void RestoreAsOf_extracts_real_content_to_a_real_destination_file()
    {
        var (hash, _) = ContentStore.StoreFromStream(new MemoryStream("old content"u8.ToArray()));
        var t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var s1 = Repository.BeginSnapshot(t0);
        Repository.RecordFileVersion(s1, "a.txt", null, hash, 11, t0, FileChangeKind.Added, t0);
        var service = new SnapshotHistoryService(Repository, ContentStore);
        var destination = Path.Combine(Path.GetTempPath(), $"vara-inttest-restore-{Guid.NewGuid():N}.txt");

        try
        {
            service.RestoreAsOf("a.txt", t0.AddDays(1), destination);

            Assert.Equal("old content", File.ReadAllText(destination));
        }
        finally
        {
            File.Delete(destination);
        }
    }

    [Fact]
    public void RestoreAsOf_refuses_a_destination_that_really_resolves_inside_the_live_mirror()
    {
        var (hash, _) = ContentStore.StoreFromStream(new MemoryStream("content"u8.ToArray()));
        var t0 = DateTimeOffset.UtcNow;
        var s1 = Repository.BeginSnapshot(t0);
        Repository.RecordFileVersion(s1, "a.txt", null, hash, 7, t0, FileChangeKind.Added, t0);
        var service = new SnapshotHistoryService(Repository, ContentStore);

        // The real FileSystemContentStore resolves mirror containment from the target
        // root passed to its constructor - no seeding required, unlike the fake's
        // MirrorPaths set.
        var mirrorDestination = Path.Combine(_targetRoot, "a.txt");

        var ex = Assert.Throws<RestoreDestinationInMirrorException>(
            () => service.RestoreAsOf("a.txt", t0.AddDays(1), mirrorDestination, overwrite: true));
        Assert.Equal(mirrorDestination, ex.DestinationPath);
    }

    [Fact]
    public void RestoreAsOf_to_a_real_existing_destination_without_overwrite_throws_DestinationExists()
    {
        var (hash, _) = ContentStore.StoreFromStream(new MemoryStream("content"u8.ToArray()));
        var t0 = DateTimeOffset.UtcNow;
        var s1 = Repository.BeginSnapshot(t0);
        Repository.RecordFileVersion(s1, "a.txt", null, hash, 7, t0, FileChangeKind.Added, t0);
        var service = new SnapshotHistoryService(Repository, ContentStore);
        var destination = Path.Combine(Path.GetTempPath(), $"vara-inttest-restore-{Guid.NewGuid():N}.txt");
        File.WriteAllText(destination, "already here");

        try
        {
            var ex = Assert.Throws<DestinationExistsException>(() => service.RestoreAsOf("a.txt", t0.AddDays(1), destination));
            Assert.Equal(destination, ex.DestinationPath);
        }
        finally
        {
            File.Delete(destination);
        }
    }

    [Fact]
    public void RestoreAsOf_to_a_real_existing_destination_with_overwrite_true_overwrites_it()
    {
        var (hash, _) = ContentStore.StoreFromStream(new MemoryStream("new content"u8.ToArray()));
        var t0 = DateTimeOffset.UtcNow;
        var s1 = Repository.BeginSnapshot(t0);
        Repository.RecordFileVersion(s1, "a.txt", null, hash, 11, t0, FileChangeKind.Added, t0);
        var service = new SnapshotHistoryService(Repository, ContentStore);
        var destination = Path.Combine(Path.GetTempPath(), $"vara-inttest-restore-{Guid.NewGuid():N}.txt");
        File.WriteAllText(destination, "stale content");

        try
        {
            service.RestoreAsOf("a.txt", t0.AddDays(1), destination, overwrite: true);

            Assert.Equal("new content", File.ReadAllText(destination));
        }
        finally
        {
            File.Delete(destination);
        }
    }

    [Fact]
    public void ListDirectory_with_includeDeleted_reflects_a_real_backup_run_followed_by_a_real_deletion()
    {
        var (hashV1, _) = ContentStore.StoreFromStream(new MemoryStream("kept"u8.ToArray()));
        var (hashV2, _) = ContentStore.StoreFromStream(new MemoryStream("gone"u8.ToArray()));
        var t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var s1 = Repository.BeginSnapshot(t0);
        Repository.RecordFileVersion(s1, @"src\keep.txt", null, hashV1, 4, t0, FileChangeKind.Added, t0);
        Repository.RecordFileVersion(s1, @"src\old.txt", null, hashV2, 4, t0, FileChangeKind.Added, t0);
        var t1 = t0.AddDays(1);
        var s2 = Repository.BeginSnapshot(t1);
        Repository.RecordFileVersion(s2, @"src\old.txt", null, hashV2, 4, t1, FileChangeKind.Deleted, t1);
        var service = new SnapshotHistoryService(Repository, ContentStore);

        var liveOnly = service.ListDirectory("src", asOf: null, includeDeleted: false);
        Assert.Single(liveOnly);

        var withDeleted = service.ListDirectory("src", asOf: null, includeDeleted: true);
        Assert.Equal(2, withDeleted.Count);
        Assert.Contains(withDeleted, e => e.Name == "old.txt" && e.Status == DirectoryEntryStatus.Deleted);
    }

    [Fact]
    public void ListDeleted_reflects_a_real_deletion_across_the_profile()
    {
        var (hash, _) = ContentStore.StoreFromStream(new MemoryStream("gone"u8.ToArray()));
        var t0 = DateTimeOffset.UtcNow;
        var s1 = Repository.BeginSnapshot(t0);
        Repository.RecordFileVersion(s1, @"src\old.txt", null, hash, 4, t0, FileChangeKind.Added, t0);
        var t1 = t0.AddDays(1);
        var s2 = Repository.BeginSnapshot(t1);
        Repository.RecordFileVersion(s2, @"src\old.txt", null, hash, 4, t1, FileChangeKind.Deleted, t1);
        var service = new SnapshotHistoryService(Repository, ContentStore);

        var deleted = service.ListDeleted(directoryPath: null, since: null);

        var entry = Assert.Single(deleted);
        Assert.Equal(@"src\old.txt", entry.RelativePath);
    }

    [Fact]
    public void ShowVersion_streams_real_stored_content_to_the_destination_stream()
    {
        var (hash, _) = ContentStore.StoreFromStream(new MemoryStream("shown"u8.ToArray()));
        var t0 = DateTimeOffset.UtcNow;
        var s1 = Repository.BeginSnapshot(t0);
        Repository.RecordFileVersion(s1, "a.txt", null, hash, 5, t0, FileChangeKind.Added, t0);
        var versionId = Repository.GetFileHistory("a.txt").Single().Id;
        var service = new SnapshotHistoryService(Repository, ContentStore);
        using var destination = new MemoryStream();

        service.ShowVersion("a.txt", versionId, asOf: null, destination);

        Assert.Equal("shown", System.Text.Encoding.UTF8.GetString(destination.ToArray()));
    }

    [Fact]
    public void OpenVersionsForDiff_opens_real_stored_content_for_both_sides()
    {
        var (hashV1, _) = ContentStore.StoreFromStream(new MemoryStream("version one"u8.ToArray()));
        var (hashV2, _) = ContentStore.StoreFromStream(new MemoryStream("version two"u8.ToArray()));
        var t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var s1 = Repository.BeginSnapshot(t0);
        Repository.RecordFileVersion(s1, "a.txt", null, hashV1, 11, t0, FileChangeKind.Added, t0);
        var t1 = t0.AddDays(1);
        var s2 = Repository.BeginSnapshot(t1);
        Repository.RecordFileVersion(s2, "a.txt", null, hashV2, 11, t1, FileChangeKind.Changed, t1);
        var versionIds = Repository.GetFileHistory("a.txt").OrderBy(r => r.Id).Select(r => r.Id).ToList();
        var service = new SnapshotHistoryService(Repository, ContentStore);

        var (left, right) = service.OpenVersionsForDiff("a.txt", versionIds[0], null, versionIds[1], null);
        using var leftReader = new StreamReader(left);
        using var rightReader = new StreamReader(right);

        Assert.Equal("version one", leftReader.ReadToEnd());
        Assert.Equal("version two", rightReader.ReadToEnd());
    }

    [Fact]
    public void OpenVersionsForDiff_refuses_an_oversized_version_against_real_stored_content()
    {
        var oversizedContent = new byte[10 * 1024 * 1024 + 1];
        Array.Fill(oversizedContent, (byte)'a');
        var (hashV1, sizeV1) = ContentStore.StoreFromStream(new MemoryStream(oversizedContent));
        var (hashV2, sizeV2) = ContentStore.StoreFromStream(new MemoryStream("version two"u8.ToArray()));
        var t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var s1 = Repository.BeginSnapshot(t0);
        Repository.RecordFileVersion(s1, "big.log", null, hashV1, sizeV1, t0, FileChangeKind.Added, t0);
        var t1 = t0.AddDays(1);
        var s2 = Repository.BeginSnapshot(t1);
        Repository.RecordFileVersion(s2, "big.log", null, hashV2, sizeV2, t1, FileChangeKind.Changed, t1);
        var versionIds = Repository.GetFileHistory("big.log").OrderBy(r => r.Id).Select(r => r.Id).ToList();
        var service = new SnapshotHistoryService(Repository, ContentStore);

        var ex = Assert.Throws<DiffContentTooLargeException>(() => service.OpenVersionsForDiff("big.log", versionIds[0], null, versionIds[1], null));

        Assert.Equal(sizeV1, ex.LeftSize);
        Assert.Null(ex.RightSize);
    }

    [Fact]
    public void OpenVersionsForDiff_refuses_binary_content_against_real_stored_content()
    {
        var binaryContent = new byte[20];
        Array.Fill(binaryContent, (byte)'x');
        binaryContent[5] = 0;
        var (hashV1, sizeV1) = ContentStore.StoreFromStream(new MemoryStream(binaryContent));
        var (hashV2, sizeV2) = ContentStore.StoreFromStream(new MemoryStream("version two"u8.ToArray()));
        var t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var s1 = Repository.BeginSnapshot(t0);
        Repository.RecordFileVersion(s1, "image.png", null, hashV1, sizeV1, t0, FileChangeKind.Added, t0);
        var t1 = t0.AddDays(1);
        var s2 = Repository.BeginSnapshot(t1);
        Repository.RecordFileVersion(s2, "image.png", null, hashV2, sizeV2, t1, FileChangeKind.Changed, t1);
        var versionIds = Repository.GetFileHistory("image.png").OrderBy(r => r.Id).Select(r => r.Id).ToList();
        var service = new SnapshotHistoryService(Repository, ContentStore);

        var ex = Assert.Throws<DiffBinaryContentException>(() => service.OpenVersionsForDiff("image.png", versionIds[0], null, versionIds[1], null));

        Assert.True(ex.LeftIsBinary);
        Assert.False(ex.RightIsBinary);
    }

    [Fact]
    public void RestoreAsOf_in_place_writes_back_to_the_original_absolute_source_path()
    {
        // Simulates restore --in-place: the CLI computes the destination via
        // AbsolutePathMirrorMapper.FromMirrorPath, then calls RestoreAsOf exactly as
        // it does for --out - this proves that reversed path is a real, writable
        // location outside the mirror that the existing guards accept.
        var sourceRoot = Path.Combine(Path.GetTempPath(), $"vara-inttest-source-{Guid.NewGuid():N}");
        Directory.CreateDirectory(sourceRoot);
        try
        {
            var mirrorRelativePath = Vara.Core.FileSystem.AbsolutePathMirrorMapper.ToMirrorPath(Path.Combine(sourceRoot, "deleted.txt"));
            var (hash, _) = ContentStore.StoreFromStream(new MemoryStream("undeleted"u8.ToArray()));
            var t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
            var s1 = Repository.BeginSnapshot(t0);
            Repository.RecordFileVersion(s1, mirrorRelativePath, null, hash, 9, t0, FileChangeKind.Added, t0);
            var t1 = t0.AddDays(1);
            var s2 = Repository.BeginSnapshot(t1);
            Repository.RecordFileVersion(s2, mirrorRelativePath, null, hash, 9, t1, FileChangeKind.Deleted, t1);
            var service = new SnapshotHistoryService(Repository, ContentStore);

            var originalSourcePath = Vara.Core.FileSystem.AbsolutePathMirrorMapper.FromMirrorPath(mirrorRelativePath);
            service.RestoreAsOf(mirrorRelativePath, t1.AddDays(-1), originalSourcePath);

            Assert.Equal("undeleted", File.ReadAllText(originalSourcePath));
        }
        finally
        {
            Directory.Delete(sourceRoot, recursive: true);
        }
    }

    [Fact]
    public void TryResolve_reproduces_the_reported_scenario_a_cwd_relative_file_is_no_longer_shadowed_by_an_unrelated_root_level_literal_match()
    {
        // Reproduces the bug report end-to-end against the real FileSystemContentStore and
        // SqliteSnapshotRepository, exactly as HistoryCommand/RestoreCommand/ShowCommand/
        // DiffCommand call SnapshotPathResolver.TryResolve: a root-level "file.txt" is
        // recorded (the unrelated file that used to win), and a distinct "Projects\file.txt"
        // is also recorded. Standing inside "<mirror>\Projects" and typing the bare literal
        // "file.txt" must now resolve to the file at the user's own location.
        var (rootHash, _) = ContentStore.StoreFromStream(new MemoryStream("unrelated root-level content"u8.ToArray()));
        var (projectsHash, _) = ContentStore.StoreFromStream(new MemoryStream("the file the user actually meant"u8.ToArray()));
        var t0 = DateTimeOffset.UtcNow;
        var s1 = Repository.BeginSnapshot(t0);
        Repository.RecordFileVersion(s1, "file.txt", null, rootHash, 28, t0, FileChangeKind.Added, t0);
        Repository.RecordFileVersion(s1, @"Projects\file.txt", null, projectsHash, 33, t0, FileChangeKind.Added, t0);

        var resolved = SnapshotPathResolver.TryResolve(
            _targetRoot,
            "file.txt",
            candidate => Repository.GetFileHistory(candidate).Count > 0,
            ContentStore.IsWithinMirror,
            out var resolvedPath,
            currentDirectory: Path.Combine(_targetRoot, "Projects"));

        Assert.True(resolved);
        Assert.Equal(@"Projects\file.txt", resolvedPath);
    }

    [Fact]
    public void PlanAndExecuteDirectoryRestore_reconstructs_a_real_point_in_time_directory_to_a_fresh_out_destination()
    {
        var (hashKept, _) = ContentStore.StoreFromStream(new MemoryStream("kept content"u8.ToArray()));
        var (hashDeletedLater, _) = ContentStore.StoreFromStream(new MemoryStream("deleted later"u8.ToArray()));
        var (hashAddedLater, _) = ContentStore.StoreFromStream(new MemoryStream("added later"u8.ToArray()));
        var t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var s1 = Repository.BeginSnapshot(t0);
        Repository.RecordFileVersion(s1, @"src\a.txt", null, hashKept, 12, t0, FileChangeKind.Added, t0);
        Repository.RecordFileVersion(s1, @"src\old.txt", null, hashDeletedLater, 13, t0, FileChangeKind.Added, t0);
        var t1 = t0.AddDays(1);
        var s2 = Repository.BeginSnapshot(t1);
        Repository.RecordFileVersion(s2, @"src\old.txt", null, hashDeletedLater, 13, t1, FileChangeKind.Deleted, t1);
        Repository.RecordFileVersion(s2, @"src\new.txt", null, hashAddedLater, 11, t1, FileChangeKind.Added, t1);
        var service = new SnapshotHistoryService(Repository, ContentStore);
        var outRoot = Path.Combine(Path.GetTempPath(), $"vara-inttest-dirrestore-{Guid.NewGuid():N}");

        try
        {
            var plan = service.PlanDirectoryRestore("src", asOf: t0.AddHours(12), outRoot, inPlace: false);
            service.ExecuteDirectoryRestore(plan);

            Assert.Equal("kept content", File.ReadAllText(Path.Combine(outRoot, "a.txt")));
            Assert.Equal("deleted later", File.ReadAllText(Path.Combine(outRoot, "old.txt")));
            Assert.False(File.Exists(Path.Combine(outRoot, "new.txt")));
        }
        finally
        {
            if (Directory.Exists(outRoot))
            {
                Directory.Delete(outRoot, recursive: true);
            }
        }
    }

    [Fact]
    public void PlanAndExecuteDirectoryRestore_in_place_removes_a_currently_live_file_not_present_at_the_requested_date_and_maps_each_source_correctly()
    {
        var sourceRootA = Path.Combine(Path.GetTempPath(), $"vara-inttest-sourceA-{Guid.NewGuid():N}");
        var sourceRootB = Path.Combine(Path.GetTempPath(), $"vara-inttest-sourceB-{Guid.NewGuid():N}");
        Directory.CreateDirectory(sourceRootA);
        Directory.CreateDirectory(sourceRootB);
        try
        {
            var pathA = Vara.Core.FileSystem.AbsolutePathMirrorMapper.ToMirrorPath(Path.Combine(sourceRootA, "a.txt"));
            var pathB = Vara.Core.FileSystem.AbsolutePathMirrorMapper.ToMirrorPath(Path.Combine(sourceRootB, "b.txt"));
            var (hashA, _) = ContentStore.StoreFromStream(new MemoryStream("from source a"u8.ToArray()));
            var (hashB, _) = ContentStore.StoreFromStream(new MemoryStream("from source b"u8.ToArray()));
            var t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
            var s1 = Repository.BeginSnapshot(t0);
            Repository.RecordFileVersion(s1, pathA, null, hashA, 13, t0, FileChangeKind.Added, t0);
            var t1 = t0.AddDays(1);
            var s2 = Repository.BeginSnapshot(t1);
            // pathB is only added after t0 - not part of the requested-date state, but it is
            // currently live, so a directory restore as of t0 should remove it in place.
            Repository.RecordFileVersion(s2, pathB, null, hashB, 13, t1, FileChangeKind.Added, t1);
            var service = new SnapshotHistoryService(Repository, ContentStore);

            // pathB currently "lives" on disk (as ContentStore.TargetExists has no real
            // filesystem backing for arbitrary destinations, seed the real file directly so
            // the plan's removal step has something real to delete).
            var originalPathB = Vara.Core.FileSystem.AbsolutePathMirrorMapper.FromMirrorPath(pathB);
            File.WriteAllText(originalPathB, "should be removed");

            var plan = service.PlanDirectoryRestore(".", asOf: t0.AddHours(12), outRoot: null, inPlace: true);
            service.ExecuteDirectoryRestore(plan);

            var originalPathA = Vara.Core.FileSystem.AbsolutePathMirrorMapper.FromMirrorPath(pathA);
            Assert.Equal("from source a", File.ReadAllText(originalPathA));
            Assert.False(File.Exists(originalPathB));
        }
        finally
        {
            Directory.Delete(sourceRootA, recursive: true);
            Directory.Delete(sourceRootB, recursive: true);
        }
    }

    [Fact]
    public void GetFileHistoryUnderPrefix_reflects_real_rows_recorded_across_multiple_snapshots()
    {
        var t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var s1 = Repository.BeginSnapshot(t0);
        Repository.RecordFileVersion(s1, @"src\a.txt", null, "hash-a", 10, t0, FileChangeKind.Added, t0);
        Repository.RecordFileVersion(s1, @"other\b.txt", null, "hash-b", 10, t0, FileChangeKind.Added, t0);
        Repository.CompleteSnapshot(s1, t0, SnapshotStats.Empty);

        var t1 = t0.AddDays(1);
        var s2 = Repository.BeginSnapshot(t1);
        Repository.RecordFileVersion(s2, @"src\a.txt", null, "hash-a2", 12, t1, FileChangeKind.Changed, t1);
        Repository.CompleteSnapshot(s2, t1, SnapshotStats.Empty);

        var history = Repository.GetFileHistoryUnderPrefix(@"src\");

        Assert.Equal(2, history.Count);
        Assert.Equal(s2, history[0].SnapshotId);
        Assert.Equal(s1, history[1].SnapshotId);
        Assert.All(history, r => Assert.StartsWith(@"src\", r.RelativePath, StringComparison.OrdinalIgnoreCase));
    }
}
