using Vara.Application.History;
using Vara.Application.Tests.Backup;
using Vara.Core.Snapshots;
using Xunit;

namespace Vara.Application.Tests.History;

public class SnapshotHistoryServiceTests
{
    [Fact]
    public void ListSnapshots_returns_recorded_snapshots_most_recent_first()
    {
        var repository = new FakeSnapshotRepository();
        var s1 = repository.BeginSnapshot(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        repository.CompleteSnapshot(s1, DateTimeOffset.UtcNow, SnapshotStats.Empty);
        var s2 = repository.BeginSnapshot(new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero));
        repository.CompleteSnapshot(s2, DateTimeOffset.UtcNow, SnapshotStats.Empty);

        var service = new SnapshotHistoryService(repository, new FakeContentStore());

        var snapshots = service.ListSnapshots();

        Assert.Equal([s2, s1], snapshots.Select(s => s.Id));
    }

    [Fact]
    public void GetFileHistory_for_a_tracked_path_returns_its_versions()
    {
        var repository = new FakeSnapshotRepository();
        var s1 = repository.BeginSnapshot(DateTimeOffset.UtcNow);
        repository.RecordFileVersion(s1, "a.txt", null, "hash-1", 10, DateTimeOffset.UtcNow, FileChangeKind.Added, DateTimeOffset.UtcNow);
        var service = new SnapshotHistoryService(repository, new FakeContentStore());

        var history = service.GetFileHistory("a.txt");

        Assert.Single(history);
    }

    [Fact]
    public void GetFileHistory_for_an_untracked_path_throws_a_clear_error()
    {
        var service = new SnapshotHistoryService(new FakeSnapshotRepository(), new FakeContentStore());

        var ex = Assert.Throws<NoHistoryForPathException>(() => service.GetFileHistory("never-tracked.txt"));
        Assert.Equal("never-tracked.txt", ex.RelativePath);
    }

    [Fact]
    public void RestoreAsOf_extracts_the_content_current_at_that_date()
    {
        var contentStore = new FakeContentStore();
        var (hash, _) = contentStore.StoreFromStream(new MemoryStream("old content"u8.ToArray()));
        var repository = new FakeSnapshotRepository();
        var t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var s1 = repository.BeginSnapshot(t0);
        repository.RecordFileVersion(s1, "a.txt", null, hash, 11, t0, FileChangeKind.Added, t0);
        var service = new SnapshotHistoryService(repository, contentStore);
        var destination = Path.Combine(Path.GetTempPath(), $"vara-restore-{Guid.NewGuid():N}.txt");

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
    public void RestoreAsOf_for_an_untracked_path_throws_NoHistoryForPath()
    {
        var service = new SnapshotHistoryService(new FakeSnapshotRepository(), new FakeContentStore());

        Assert.Throws<NoHistoryForPathException>(() => service.RestoreAsOf("never.txt", DateTimeOffset.UtcNow, "out.txt"));
    }

    [Fact]
    public void RestoreAsOf_before_the_path_existed_throws_NoMatchingVersion()
    {
        var repository = new FakeSnapshotRepository();
        var t0 = new DateTimeOffset(2026, 1, 10, 0, 0, 0, TimeSpan.Zero);
        var s1 = repository.BeginSnapshot(t0);
        repository.RecordFileVersion(s1, "a.txt", null, "hash-1", 10, t0, FileChangeKind.Added, t0);
        var service = new SnapshotHistoryService(repository, new FakeContentStore());

        Assert.Throws<NoMatchingVersionException>(() => service.RestoreAsOf("a.txt", t0.AddDays(-1), "out.txt"));
    }

    [Fact]
    public void RestoreAsOf_after_a_deletion_throws_NoMatchingVersion()
    {
        var repository = new FakeSnapshotRepository();
        var t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var s1 = repository.BeginSnapshot(t0);
        repository.RecordFileVersion(s1, "a.txt", null, "hash-1", 10, t0, FileChangeKind.Added, t0);
        var t1 = t0.AddDays(1);
        var s2 = repository.BeginSnapshot(t1);
        repository.RecordFileVersion(s2, "a.txt", null, "hash-1", 10, t1, FileChangeKind.Deleted, t1);
        var service = new SnapshotHistoryService(repository, new FakeContentStore());

        Assert.Throws<NoMatchingVersionException>(() => service.RestoreAsOf("a.txt", t1.AddDays(1), "out.txt"));
    }

    [Fact]
    public void RestoreAsOf_a_deleted_file_before_its_deletion_still_restores_it()
    {
        var contentStore = new FakeContentStore();
        var (hash, _) = contentStore.StoreFromStream(new MemoryStream("still here"u8.ToArray()));
        var repository = new FakeSnapshotRepository();
        var t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var s1 = repository.BeginSnapshot(t0);
        repository.RecordFileVersion(s1, "a.txt", null, hash, 10, t0, FileChangeKind.Added, t0);
        var t1 = t0.AddDays(1);
        var s2 = repository.BeginSnapshot(t1);
        repository.RecordFileVersion(s2, "a.txt", null, hash, 10, t1, FileChangeKind.Deleted, t1);
        var service = new SnapshotHistoryService(repository, contentStore);
        var destination = Path.Combine(Path.GetTempPath(), $"vara-restore-{Guid.NewGuid():N}.txt");

        try
        {
            service.RestoreAsOf("a.txt", t0.AddHours(12), destination);

            Assert.Equal("still here", File.ReadAllText(destination));
        }
        finally
        {
            File.Delete(destination);
        }
    }

    [Fact]
    public void RestoreVersion_extracts_the_content_of_the_specific_version_id()
    {
        var contentStore = new FakeContentStore();
        var (hash, _) = contentStore.StoreFromStream(new MemoryStream("versioned content"u8.ToArray()));
        var repository = new FakeSnapshotRepository();
        var t0 = DateTimeOffset.UtcNow;
        var s1 = repository.BeginSnapshot(t0);
        repository.RecordFileVersion(s1, "a.txt", null, hash, 17, t0, FileChangeKind.Added, t0);
        var versionId = repository.GetFileHistory("a.txt").Single().Id;
        var service = new SnapshotHistoryService(repository, contentStore);
        var destination = Path.Combine(Path.GetTempPath(), $"vara-restore-{Guid.NewGuid():N}.txt");

        try
        {
            service.RestoreVersion("a.txt", versionId, destination);

            Assert.Equal("versioned content", File.ReadAllText(destination));
        }
        finally
        {
            File.Delete(destination);
        }
    }

    [Fact]
    public void RestoreAsOf_invokes_onBytesCopied_incrementally_and_onSizeResolved_with_the_matched_versions_size()
    {
        var contentStore = new FakeContentStore();
        var largeContent = new byte[64 * 1024];
        Random.Shared.NextBytes(largeContent);
        var (hash, size) = contentStore.StoreFromStream(new MemoryStream(largeContent));
        var repository = new FakeSnapshotRepository();
        var t0 = DateTimeOffset.UtcNow;
        var s1 = repository.BeginSnapshot(t0);
        repository.RecordFileVersion(s1, "a.txt", null, hash, size, t0, FileChangeKind.Added, t0);
        var service = new SnapshotHistoryService(repository, contentStore);
        var destination = Path.Combine(Path.GetTempPath(), $"vara-restore-{Guid.NewGuid():N}.txt");

        try
        {
            var chunkReports = new List<long>();
            long? resolvedSize = null;

            service.RestoreAsOf(
                "a.txt",
                t0.AddDays(1),
                destination,
                onBytesCopied: bytes => chunkReports.Add(bytes),
                onSizeResolved: resolved => resolvedSize = resolved);

            Assert.True(chunkReports.Count > 1, $"expected more than one progress report for a multi-chunk file, observed {chunkReports.Count}");
            Assert.Equal(size, chunkReports.Sum());
            Assert.Equal(size, resolvedSize);
        }
        finally
        {
            File.Delete(destination);
        }
    }

    [Fact]
    public void RestoreVersion_with_an_unknown_id_throws_NoMatchingVersion()
    {
        var repository = new FakeSnapshotRepository();
        var s1 = repository.BeginSnapshot(DateTimeOffset.UtcNow);
        repository.RecordFileVersion(s1, "a.txt", null, "hash", 10, DateTimeOffset.UtcNow, FileChangeKind.Added, DateTimeOffset.UtcNow);
        var service = new SnapshotHistoryService(repository, new FakeContentStore());

        Assert.Throws<NoMatchingVersionException>(() => service.RestoreVersion("a.txt", 9999, "out.txt"));
    }

    [Fact]
    public void RestoreVersion_of_a_linked_entry_throws_RestoreLinkedEntry_instead_of_extracting()
    {
        var repository = new FakeSnapshotRepository();
        var t0 = DateTimeOffset.UtcNow;
        var s1 = repository.BeginSnapshot(t0);
        repository.RecordFileVersion(s1, "link", null, null, 0, t0, FileChangeKind.Linked, t0, linkTarget: @"C:\target");
        var versionId = repository.GetFileHistory("link").Single().Id;
        var service = new SnapshotHistoryService(repository, new FakeContentStore());

        var ex = Assert.Throws<RestoreLinkedEntryException>(() => service.RestoreVersion("link", versionId, "out.txt"));

        Assert.Equal("link", ex.RelativePath);
    }

    [Fact]
    public void RestoreAsOf_of_a_linked_entry_throws_RestoreLinkedEntry_instead_of_extracting()
    {
        var repository = new FakeSnapshotRepository();
        var t0 = DateTimeOffset.UtcNow;
        var s1 = repository.BeginSnapshot(t0);
        repository.RecordFileVersion(s1, "link", null, null, 0, t0, FileChangeKind.Linked, t0, linkTarget: @"C:\target");
        var service = new SnapshotHistoryService(repository, new FakeContentStore());

        var ex = Assert.Throws<RestoreLinkedEntryException>(() => service.RestoreAsOf("link", t0.AddDays(1), "out.txt"));

        Assert.Equal("link", ex.RelativePath);
    }

    [Fact]
    public void RestoreAsOf_to_a_destination_within_the_mirror_throws_even_when_overwrite_is_true()
    {
        var contentStore = new FakeContentStore();
        var (hash, _) = contentStore.StoreFromStream(new MemoryStream("content"u8.ToArray()));
        contentStore.MirrorPaths.Add(@"C:\mirror\a.txt");
        var repository = new FakeSnapshotRepository();
        var t0 = DateTimeOffset.UtcNow;
        var s1 = repository.BeginSnapshot(t0);
        repository.RecordFileVersion(s1, "a.txt", null, hash, 7, t0, FileChangeKind.Added, t0);
        var service = new SnapshotHistoryService(repository, contentStore);

        var ex = Assert.Throws<RestoreDestinationInMirrorException>(
            () => service.RestoreAsOf("a.txt", t0.AddDays(1), @"C:\mirror\a.txt", overwrite: true));
        Assert.Equal(@"C:\mirror\a.txt", ex.DestinationPath);
    }

    [Fact]
    public void RestoreAsOf_to_an_existing_destination_without_overwrite_throws_DestinationExists()
    {
        var contentStore = new FakeContentStore();
        var (hash, _) = contentStore.StoreFromStream(new MemoryStream("content"u8.ToArray()));
        contentStore.SeedExistingTarget("out.txt");
        var repository = new FakeSnapshotRepository();
        var t0 = DateTimeOffset.UtcNow;
        var s1 = repository.BeginSnapshot(t0);
        repository.RecordFileVersion(s1, "a.txt", null, hash, 7, t0, FileChangeKind.Added, t0);
        var service = new SnapshotHistoryService(repository, contentStore);

        var ex = Assert.Throws<DestinationExistsException>(() => service.RestoreAsOf("a.txt", t0.AddDays(1), "out.txt"));
        Assert.Equal("out.txt", ex.DestinationPath);
    }

    [Fact]
    public void RestoreAsOf_to_an_existing_destination_with_overwrite_true_overwrites_it()
    {
        var contentStore = new FakeContentStore();
        var (hash, _) = contentStore.StoreFromStream(new MemoryStream("new content"u8.ToArray()));
        contentStore.SeedExistingTarget("out.txt");
        var repository = new FakeSnapshotRepository();
        var t0 = DateTimeOffset.UtcNow;
        var s1 = repository.BeginSnapshot(t0);
        repository.RecordFileVersion(s1, "a.txt", null, hash, 11, t0, FileChangeKind.Added, t0);
        var service = new SnapshotHistoryService(repository, contentStore);
        var destination = Path.Combine(Path.GetTempPath(), $"vara-restore-{Guid.NewGuid():N}.txt");
        contentStore.SeedExistingTarget(destination);

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
    public void RestoreVersion_to_a_destination_within_the_mirror_throws_even_when_overwrite_is_true()
    {
        var contentStore = new FakeContentStore();
        var (hash, _) = contentStore.StoreFromStream(new MemoryStream("content"u8.ToArray()));
        contentStore.MirrorPaths.Add(@"C:\mirror\a.txt");
        var repository = new FakeSnapshotRepository();
        var s1 = repository.BeginSnapshot(DateTimeOffset.UtcNow);
        repository.RecordFileVersion(s1, "a.txt", null, hash, 7, DateTimeOffset.UtcNow, FileChangeKind.Added, DateTimeOffset.UtcNow);
        var versionId = repository.GetFileHistory("a.txt").Single().Id;
        var service = new SnapshotHistoryService(repository, contentStore);

        var ex = Assert.Throws<RestoreDestinationInMirrorException>(
            () => service.RestoreVersion("a.txt", versionId, @"C:\mirror\a.txt", overwrite: true));
        Assert.Equal(@"C:\mirror\a.txt", ex.DestinationPath);
    }

    [Fact]
    public void RestoreVersion_to_an_existing_destination_without_overwrite_throws_DestinationExists()
    {
        var contentStore = new FakeContentStore();
        var (hash, _) = contentStore.StoreFromStream(new MemoryStream("content"u8.ToArray()));
        contentStore.SeedExistingTarget("out.txt");
        var repository = new FakeSnapshotRepository();
        var s1 = repository.BeginSnapshot(DateTimeOffset.UtcNow);
        repository.RecordFileVersion(s1, "a.txt", null, hash, 7, DateTimeOffset.UtcNow, FileChangeKind.Added, DateTimeOffset.UtcNow);
        var versionId = repository.GetFileHistory("a.txt").Single().Id;
        var service = new SnapshotHistoryService(repository, contentStore);

        var ex = Assert.Throws<DestinationExistsException>(() => service.RestoreVersion("a.txt", versionId, "out.txt"));
        Assert.Equal("out.txt", ex.DestinationPath);
    }

    [Fact]
    public void RestoreVersion_to_an_existing_destination_with_overwrite_true_overwrites_it()
    {
        var contentStore = new FakeContentStore();
        var (hash, _) = contentStore.StoreFromStream(new MemoryStream("versioned content"u8.ToArray()));
        var repository = new FakeSnapshotRepository();
        var t0 = DateTimeOffset.UtcNow;
        var s1 = repository.BeginSnapshot(t0);
        repository.RecordFileVersion(s1, "a.txt", null, hash, 17, t0, FileChangeKind.Added, t0);
        var versionId = repository.GetFileHistory("a.txt").Single().Id;
        var service = new SnapshotHistoryService(repository, contentStore);
        var destination = Path.Combine(Path.GetTempPath(), $"vara-restore-{Guid.NewGuid():N}.txt");
        contentStore.SeedExistingTarget(destination);

        try
        {
            service.RestoreVersion("a.txt", versionId, destination, overwrite: true);

            Assert.Equal("versioned content", File.ReadAllText(destination));
        }
        finally
        {
            File.Delete(destination);
        }
    }

    [Fact]
    public void ShowVersion_writes_the_resolved_versions_content_to_the_destination_stream()
    {
        var contentStore = new FakeContentStore();
        var (hash, _) = contentStore.StoreFromStream(new MemoryStream("shown content"u8.ToArray()));
        var repository = new FakeSnapshotRepository();
        var t0 = DateTimeOffset.UtcNow;
        var s1 = repository.BeginSnapshot(t0);
        repository.RecordFileVersion(s1, "a.txt", null, hash, 13, t0, FileChangeKind.Added, t0);
        var versionId = repository.GetFileHistory("a.txt").Single().Id;
        var service = new SnapshotHistoryService(repository, contentStore);
        using var destination = new MemoryStream();

        service.ShowVersion("a.txt", versionId, asOf: null, destination);

        Assert.Equal("shown content", System.Text.Encoding.UTF8.GetString(destination.ToArray()));
    }

    [Fact]
    public void ShowVersion_for_an_untracked_path_throws_NoHistoryForPath()
    {
        var service = new SnapshotHistoryService(new FakeSnapshotRepository(), new FakeContentStore());

        Assert.Throws<NoHistoryForPathException>(() => service.ShowVersion("never.txt", 1, null, new MemoryStream()));
    }

    [Fact]
    public void ShowVersion_with_an_unknown_version_id_throws_NoMatchingVersion()
    {
        var repository = new FakeSnapshotRepository();
        var s1 = repository.BeginSnapshot(DateTimeOffset.UtcNow);
        repository.RecordFileVersion(s1, "a.txt", null, "hash", 10, DateTimeOffset.UtcNow, FileChangeKind.Added, DateTimeOffset.UtcNow);
        var service = new SnapshotHistoryService(repository, new FakeContentStore());

        Assert.Throws<NoMatchingVersionException>(() => service.ShowVersion("a.txt", 9999, null, new MemoryStream()));
    }

    [Fact]
    public void ShowVersion_with_binary_content_and_forceBinary_false_throws_before_writing_any_bytes()
    {
        var contentStore = new FakeContentStore();
        var (hash, _) = contentStore.StoreFromStream(new MemoryStream(BinaryContent(nulOffset: 5, totalLength: 20)));
        var repository = new FakeSnapshotRepository();
        var t0 = DateTimeOffset.UtcNow;
        var s1 = repository.BeginSnapshot(t0);
        repository.RecordFileVersion(s1, "a.bin", null, hash, 20, t0, FileChangeKind.Added, t0);
        var versionId = repository.GetFileHistory("a.bin").Single().Id;
        var service = new SnapshotHistoryService(repository, contentStore);
        using var destination = new MemoryStream();

        Assert.Throws<ShowBinaryContentException>(() => service.ShowVersion("a.bin", versionId, asOf: null, destination, forceBinary: false));

        Assert.Empty(destination.ToArray());
    }

    [Fact]
    public void ShowVersion_with_binary_content_and_forceBinary_true_streams_it_exactly_as_before()
    {
        var contentStore = new FakeContentStore();
        var content = BinaryContent(nulOffset: 5, totalLength: 20);
        var (hash, _) = contentStore.StoreFromStream(new MemoryStream(content));
        var repository = new FakeSnapshotRepository();
        var t0 = DateTimeOffset.UtcNow;
        var s1 = repository.BeginSnapshot(t0);
        repository.RecordFileVersion(s1, "a.bin", null, hash, 20, t0, FileChangeKind.Added, t0);
        var versionId = repository.GetFileHistory("a.bin").Single().Id;
        var service = new SnapshotHistoryService(repository, contentStore);
        using var destination = new MemoryStream();

        service.ShowVersion("a.bin", versionId, asOf: null, destination, forceBinary: true);

        Assert.Equal(content, destination.ToArray());
    }

    [Fact]
    public void OpenVersionsForDiff_resolves_each_side_independently()
    {
        var contentStore = new FakeContentStore();
        var (hashV1, _) = contentStore.StoreFromStream(new MemoryStream("version one"u8.ToArray()));
        var (hashV2, _) = contentStore.StoreFromStream(new MemoryStream("version two"u8.ToArray()));
        var repository = new FakeSnapshotRepository();
        var t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var s1 = repository.BeginSnapshot(t0);
        repository.RecordFileVersion(s1, "a.txt", null, hashV1, 11, t0, FileChangeKind.Added, t0);
        var t1 = t0.AddDays(1);
        var s2 = repository.BeginSnapshot(t1);
        repository.RecordFileVersion(s2, "a.txt", null, hashV2, 11, t1, FileChangeKind.Changed, t1);
        var versionIds = repository.GetFileHistory("a.txt").OrderBy(r => r.Id).Select(r => r.Id).ToList();
        var service = new SnapshotHistoryService(repository, contentStore);

        var (left, right) = service.OpenVersionsForDiff("a.txt", versionIds[0], null, versionIds[1], null);
        using var leftReader = new StreamReader(left);
        using var rightReader = new StreamReader(right);

        Assert.Equal("version one", leftReader.ReadToEnd());
        Assert.Equal("version two", rightReader.ReadToEnd());
    }

    [Fact]
    public void OpenVersionsForDiff_with_an_unknown_version_on_either_side_throws_NoMatchingVersion()
    {
        var repository = new FakeSnapshotRepository();
        var s1 = repository.BeginSnapshot(DateTimeOffset.UtcNow);
        repository.RecordFileVersion(s1, "a.txt", null, "hash", 10, DateTimeOffset.UtcNow, FileChangeKind.Added, DateTimeOffset.UtcNow);
        var versionId = repository.GetFileHistory("a.txt").Single().Id;
        var service = new SnapshotHistoryService(repository, new FakeContentStore());

        Assert.Throws<NoMatchingVersionException>(() => service.OpenVersionsForDiff("a.txt", versionId, null, 9999, null));
    }

    private const long OversizedForDiff = 10 * 1024 * 1024 + 1;

    [Fact]
    public void OpenVersionsForDiff_with_only_the_left_side_oversized_throws_DiffContentTooLarge()
    {
        var contentStore = new FakeContentStore();
        var (hashV1, _) = contentStore.StoreFromStream(new MemoryStream("version one"u8.ToArray()));
        var (hashV2, _) = contentStore.StoreFromStream(new MemoryStream("version two"u8.ToArray()));
        var repository = new FakeSnapshotRepository();
        var t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var s1 = repository.BeginSnapshot(t0);
        repository.RecordFileVersion(s1, "a.txt", null, hashV1, OversizedForDiff, t0, FileChangeKind.Added, t0);
        var t1 = t0.AddDays(1);
        var s2 = repository.BeginSnapshot(t1);
        repository.RecordFileVersion(s2, "a.txt", null, hashV2, 11, t1, FileChangeKind.Changed, t1);
        var versionIds = repository.GetFileHistory("a.txt").OrderBy(r => r.Id).Select(r => r.Id).ToList();
        var service = new SnapshotHistoryService(repository, contentStore);

        var ex = Assert.Throws<DiffContentTooLargeException>(() => service.OpenVersionsForDiff("a.txt", versionIds[0], null, versionIds[1], null));

        Assert.Equal(OversizedForDiff, ex.LeftSize);
        Assert.Null(ex.RightSize);
    }

    [Fact]
    public void OpenVersionsForDiff_with_only_the_right_side_oversized_throws_DiffContentTooLarge()
    {
        var contentStore = new FakeContentStore();
        var (hashV1, _) = contentStore.StoreFromStream(new MemoryStream("version one"u8.ToArray()));
        var (hashV2, _) = contentStore.StoreFromStream(new MemoryStream("version two"u8.ToArray()));
        var repository = new FakeSnapshotRepository();
        var t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var s1 = repository.BeginSnapshot(t0);
        repository.RecordFileVersion(s1, "a.txt", null, hashV1, 11, t0, FileChangeKind.Added, t0);
        var t1 = t0.AddDays(1);
        var s2 = repository.BeginSnapshot(t1);
        repository.RecordFileVersion(s2, "a.txt", null, hashV2, OversizedForDiff, t1, FileChangeKind.Changed, t1);
        var versionIds = repository.GetFileHistory("a.txt").OrderBy(r => r.Id).Select(r => r.Id).ToList();
        var service = new SnapshotHistoryService(repository, contentStore);

        var ex = Assert.Throws<DiffContentTooLargeException>(() => service.OpenVersionsForDiff("a.txt", versionIds[0], null, versionIds[1], null));

        Assert.Null(ex.LeftSize);
        Assert.Equal(OversizedForDiff, ex.RightSize);
    }

    [Fact]
    public void OpenVersionsForDiff_with_both_sides_oversized_throws_DiffContentTooLarge_naming_both()
    {
        var contentStore = new FakeContentStore();
        var (hashV1, _) = contentStore.StoreFromStream(new MemoryStream("version one"u8.ToArray()));
        var (hashV2, _) = contentStore.StoreFromStream(new MemoryStream("version two"u8.ToArray()));
        var repository = new FakeSnapshotRepository();
        var t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var s1 = repository.BeginSnapshot(t0);
        repository.RecordFileVersion(s1, "a.txt", null, hashV1, OversizedForDiff, t0, FileChangeKind.Added, t0);
        var t1 = t0.AddDays(1);
        var s2 = repository.BeginSnapshot(t1);
        repository.RecordFileVersion(s2, "a.txt", null, hashV2, OversizedForDiff + 1, t1, FileChangeKind.Changed, t1);
        var versionIds = repository.GetFileHistory("a.txt").OrderBy(r => r.Id).Select(r => r.Id).ToList();
        var service = new SnapshotHistoryService(repository, contentStore);

        var ex = Assert.Throws<DiffContentTooLargeException>(() => service.OpenVersionsForDiff("a.txt", versionIds[0], null, versionIds[1], null));

        Assert.Equal(OversizedForDiff, ex.LeftSize);
        Assert.Equal(OversizedForDiff + 1, ex.RightSize);
    }

    private static byte[] BinaryContent(int nulOffset, int totalLength)
    {
        var bytes = new byte[totalLength];
        Array.Fill(bytes, (byte)'x');
        bytes[nulOffset] = 0;
        return bytes;
    }

    [Fact]
    public void OpenVersionsForDiff_with_only_the_left_side_binary_throws_DiffBinaryContent()
    {
        var contentStore = new FakeContentStore();
        var (hashV1, _) = contentStore.StoreFromStream(new MemoryStream(BinaryContent(nulOffset: 5, totalLength: 20)));
        var (hashV2, _) = contentStore.StoreFromStream(new MemoryStream("version two"u8.ToArray()));
        var repository = new FakeSnapshotRepository();
        var t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var s1 = repository.BeginSnapshot(t0);
        repository.RecordFileVersion(s1, "a.txt", null, hashV1, 20, t0, FileChangeKind.Added, t0);
        var t1 = t0.AddDays(1);
        var s2 = repository.BeginSnapshot(t1);
        repository.RecordFileVersion(s2, "a.txt", null, hashV2, 11, t1, FileChangeKind.Changed, t1);
        var versionIds = repository.GetFileHistory("a.txt").OrderBy(r => r.Id).Select(r => r.Id).ToList();
        var service = new SnapshotHistoryService(repository, contentStore);

        var ex = Assert.Throws<DiffBinaryContentException>(() => service.OpenVersionsForDiff("a.txt", versionIds[0], null, versionIds[1], null));

        Assert.True(ex.LeftIsBinary);
        Assert.False(ex.RightIsBinary);
    }

    [Fact]
    public void OpenVersionsForDiff_with_only_the_right_side_binary_throws_DiffBinaryContent()
    {
        var contentStore = new FakeContentStore();
        var (hashV1, _) = contentStore.StoreFromStream(new MemoryStream("version one"u8.ToArray()));
        var (hashV2, _) = contentStore.StoreFromStream(new MemoryStream(BinaryContent(nulOffset: 5, totalLength: 20)));
        var repository = new FakeSnapshotRepository();
        var t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var s1 = repository.BeginSnapshot(t0);
        repository.RecordFileVersion(s1, "a.txt", null, hashV1, 11, t0, FileChangeKind.Added, t0);
        var t1 = t0.AddDays(1);
        var s2 = repository.BeginSnapshot(t1);
        repository.RecordFileVersion(s2, "a.txt", null, hashV2, 20, t1, FileChangeKind.Changed, t1);
        var versionIds = repository.GetFileHistory("a.txt").OrderBy(r => r.Id).Select(r => r.Id).ToList();
        var service = new SnapshotHistoryService(repository, contentStore);

        var ex = Assert.Throws<DiffBinaryContentException>(() => service.OpenVersionsForDiff("a.txt", versionIds[0], null, versionIds[1], null));

        Assert.False(ex.LeftIsBinary);
        Assert.True(ex.RightIsBinary);
    }

    [Fact]
    public void OpenVersionsForDiff_with_both_sides_binary_throws_DiffBinaryContent_naming_both()
    {
        var contentStore = new FakeContentStore();
        var (hashV1, _) = contentStore.StoreFromStream(new MemoryStream(BinaryContent(nulOffset: 3, totalLength: 20)));
        var (hashV2, _) = contentStore.StoreFromStream(new MemoryStream(BinaryContent(nulOffset: 7, totalLength: 20)));
        var repository = new FakeSnapshotRepository();
        var t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var s1 = repository.BeginSnapshot(t0);
        repository.RecordFileVersion(s1, "a.txt", null, hashV1, 20, t0, FileChangeKind.Added, t0);
        var t1 = t0.AddDays(1);
        var s2 = repository.BeginSnapshot(t1);
        repository.RecordFileVersion(s2, "a.txt", null, hashV2, 20, t1, FileChangeKind.Changed, t1);
        var versionIds = repository.GetFileHistory("a.txt").OrderBy(r => r.Id).Select(r => r.Id).ToList();
        var service = new SnapshotHistoryService(repository, contentStore);

        var ex = Assert.Throws<DiffBinaryContentException>(() => service.OpenVersionsForDiff("a.txt", versionIds[0], null, versionIds[1], null));

        Assert.True(ex.LeftIsBinary);
        Assert.True(ex.RightIsBinary);
    }

    [Fact]
    public void OpenVersionsForDiff_with_a_NUL_byte_beyond_the_binary_sample_still_diffs_and_rewinds_the_stream()
    {
        // A NUL byte placed after the first 8000 bytes must not trip the binary check, and the
        // sample read used to check for it must not leave the returned stream partially consumed.
        var content = new byte[9000];
        Array.Fill(content, (byte)'x');
        content[8500] = 0;
        var contentStore = new FakeContentStore();
        var (hashV1, _) = contentStore.StoreFromStream(new MemoryStream(content));
        var (hashV2, _) = contentStore.StoreFromStream(new MemoryStream("version two"u8.ToArray()));
        var repository = new FakeSnapshotRepository();
        var t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var s1 = repository.BeginSnapshot(t0);
        repository.RecordFileVersion(s1, "a.txt", null, hashV1, content.Length, t0, FileChangeKind.Added, t0);
        var t1 = t0.AddDays(1);
        var s2 = repository.BeginSnapshot(t1);
        repository.RecordFileVersion(s2, "a.txt", null, hashV2, 11, t1, FileChangeKind.Changed, t1);
        var versionIds = repository.GetFileHistory("a.txt").OrderBy(r => r.Id).Select(r => r.Id).ToList();
        var service = new SnapshotHistoryService(repository, contentStore);

        var (left, right) = service.OpenVersionsForDiff("a.txt", versionIds[0], null, versionIds[1], null);
        using var leftStream = left;
        using var rightStream = right;
        using var leftBuffer = new MemoryStream();
        leftStream.CopyTo(leftBuffer);

        Assert.Equal(content, leftBuffer.ToArray());
    }

    [Fact]
    public void OpenVersionsForDiff_with_one_side_oversized_and_the_other_binary_reports_only_the_size_error()
    {
        // Size is checked before binary sampling, so a comparison already refused on size never
        // even samples the other side for binary content.
        var contentStore = new FakeContentStore();
        var (hashV1, _) = contentStore.StoreFromStream(new MemoryStream("version one"u8.ToArray()));
        var (hashV2, _) = contentStore.StoreFromStream(new MemoryStream(BinaryContent(nulOffset: 5, totalLength: 20)));
        var repository = new FakeSnapshotRepository();
        var t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var s1 = repository.BeginSnapshot(t0);
        repository.RecordFileVersion(s1, "a.txt", null, hashV1, OversizedForDiff, t0, FileChangeKind.Added, t0);
        var t1 = t0.AddDays(1);
        var s2 = repository.BeginSnapshot(t1);
        repository.RecordFileVersion(s2, "a.txt", null, hashV2, 20, t1, FileChangeKind.Changed, t1);
        var versionIds = repository.GetFileHistory("a.txt").OrderBy(r => r.Id).Select(r => r.Id).ToList();
        var service = new SnapshotHistoryService(repository, contentStore);

        var ex = Assert.Throws<DiffContentTooLargeException>(() => service.OpenVersionsForDiff("a.txt", versionIds[0], null, versionIds[1], null));

        Assert.Equal(OversizedForDiff, ex.LeftSize);
        Assert.Null(ex.RightSize);
    }

    [Fact]
    public void ShowVersion_of_a_linked_entry_throws_ShowOrDiffLinkedEntry_instead_of_MissingContentHash()
    {
        var repository = new FakeSnapshotRepository();
        var t0 = DateTimeOffset.UtcNow;
        var s1 = repository.BeginSnapshot(t0);
        repository.RecordFileVersion(s1, "link", null, null, 0, t0, FileChangeKind.Linked, t0, linkTarget: @"C:\target");
        var versionId = repository.GetFileHistory("link").Single().Id;
        var service = new SnapshotHistoryService(repository, new FakeContentStore());

        var ex = Assert.Throws<ShowOrDiffLinkedEntryException>(() => service.ShowVersion("link", versionId, asOf: null, new MemoryStream()));

        Assert.Equal("link", ex.RelativePath);
        Assert.Null(ex.LeftIsLinked);
        Assert.Null(ex.RightIsLinked);
    }

    [Fact]
    public void OpenVersionsForDiff_with_only_the_left_side_linked_throws_ShowOrDiffLinkedEntry_naming_the_left_side()
    {
        var contentStore = new FakeContentStore();
        var (hash, _) = contentStore.StoreFromStream(new MemoryStream("version two"u8.ToArray()));
        var repository = new FakeSnapshotRepository();
        var t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var s1 = repository.BeginSnapshot(t0);
        repository.RecordFileVersion(s1, "link", null, null, 0, t0, FileChangeKind.Linked, t0, linkTarget: @"C:\target");
        var t1 = t0.AddDays(1);
        var s2 = repository.BeginSnapshot(t1);
        repository.RecordFileVersion(s2, "link", null, hash, 11, t1, FileChangeKind.Changed, t1);
        var versionIds = repository.GetFileHistory("link").OrderBy(r => r.Id).Select(r => r.Id).ToList();
        var service = new SnapshotHistoryService(repository, contentStore);

        var ex = Assert.Throws<ShowOrDiffLinkedEntryException>(() => service.OpenVersionsForDiff("link", versionIds[0], null, versionIds[1], null));

        Assert.True(ex.LeftIsLinked);
        Assert.False(ex.RightIsLinked);
    }

    [Fact]
    public void OpenVersionsForDiff_with_only_the_right_side_linked_throws_ShowOrDiffLinkedEntry_naming_the_right_side()
    {
        var contentStore = new FakeContentStore();
        var (hash, _) = contentStore.StoreFromStream(new MemoryStream("version one"u8.ToArray()));
        var repository = new FakeSnapshotRepository();
        var t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var s1 = repository.BeginSnapshot(t0);
        repository.RecordFileVersion(s1, "link", null, hash, 11, t0, FileChangeKind.Added, t0);
        var t1 = t0.AddDays(1);
        var s2 = repository.BeginSnapshot(t1);
        repository.RecordFileVersion(s2, "link", null, null, 0, t1, FileChangeKind.Linked, t1, linkTarget: @"C:\target");
        var versionIds = repository.GetFileHistory("link").OrderBy(r => r.Id).Select(r => r.Id).ToList();
        var service = new SnapshotHistoryService(repository, contentStore);

        var ex = Assert.Throws<ShowOrDiffLinkedEntryException>(() => service.OpenVersionsForDiff("link", versionIds[0], null, versionIds[1], null));

        Assert.False(ex.LeftIsLinked);
        Assert.True(ex.RightIsLinked);
    }

    [Fact]
    public void OpenVersionsForDiff_disposes_the_already_opened_left_stream_when_the_right_side_fails_to_open()
    {
        var contentStore = new FakeContentStore();
        var (hashV1, _) = contentStore.StoreFromStream(new MemoryStream("version one"u8.ToArray()));
        var (hashV2, _) = contentStore.StoreFromStream(new MemoryStream("version two"u8.ToArray()));
        contentStore.ThrowOnOpenReadForHash = hashV2;
        var repository = new FakeSnapshotRepository();
        var t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var s1 = repository.BeginSnapshot(t0);
        repository.RecordFileVersion(s1, "a.txt", null, hashV1, 11, t0, FileChangeKind.Added, t0);
        var t1 = t0.AddDays(1);
        var s2 = repository.BeginSnapshot(t1);
        repository.RecordFileVersion(s2, "a.txt", null, hashV2, 11, t1, FileChangeKind.Changed, t1);
        var versionIds = repository.GetFileHistory("a.txt").OrderBy(r => r.Id).Select(r => r.Id).ToList();
        var service = new SnapshotHistoryService(repository, contentStore);

        Assert.Throws<FileNotFoundException>(() => service.OpenVersionsForDiff("a.txt", versionIds[0], null, versionIds[1], null));

        Assert.Contains(hashV1, contentStore.DisposedHashes);
    }

    [Fact]
    public void ListDirectory_returns_only_live_entries_by_default()
    {
        var repository = new FakeSnapshotRepository();
        var t0 = DateTimeOffset.UtcNow;
        var s1 = repository.BeginSnapshot(t0);
        repository.RecordFileVersion(s1, @"src\main.py", null, "hash-1", 10, t0, FileChangeKind.Added, t0);
        repository.RecordFileVersion(s1, @"src\utils\helper.py", null, "hash-2", 20, t0, FileChangeKind.Added, t0);
        repository.RecordFileVersion(s1, @"notes.txt", null, "hash-3", 5, t0, FileChangeKind.Added, t0);
        var service = new SnapshotHistoryService(repository, new FakeContentStore());

        var entries = service.ListDirectory("src", asOf: null, includeDeleted: false);

        Assert.Equal(2, entries.Count);
        var file = Assert.Single(entries, e => e.Name == "main.py");
        Assert.Equal(DirectoryEntryKind.File, file.Kind);
        Assert.Equal(DirectoryEntryStatus.Live, file.Status);
        var dir = Assert.Single(entries, e => e.Name == "utils");
        Assert.Equal(DirectoryEntryKind.Directory, dir.Kind);
        Assert.Equal(DirectoryEntryStatus.Live, dir.Status);
    }

    [Fact]
    public void ListDirectory_as_of_a_past_date_excludes_entries_added_later()
    {
        var repository = new FakeSnapshotRepository();
        var t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var s1 = repository.BeginSnapshot(t0);
        repository.RecordFileVersion(s1, @"src\main.py", null, "hash-1", 10, t0, FileChangeKind.Added, t0);
        var t1 = t0.AddDays(1);
        var s2 = repository.BeginSnapshot(t1);
        repository.RecordFileVersion(s2, @"src\new_file.py", null, "hash-2", 5, t1, FileChangeKind.Added, t1);
        var service = new SnapshotHistoryService(repository, new FakeContentStore());

        var entries = service.ListDirectory("src", asOf: t0.AddHours(12), includeDeleted: false);

        Assert.Single(entries, e => e.Name == "main.py");
        Assert.DoesNotContain(entries, e => e.Name == "new_file.py");
    }

    [Fact]
    public void ListDirectory_with_includeDeleted_interleaves_a_deleted_entry_marked_distinctly()
    {
        var repository = new FakeSnapshotRepository();
        var t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var s1 = repository.BeginSnapshot(t0);
        repository.RecordFileVersion(s1, @"src\main.py", null, "hash-1", 10, t0, FileChangeKind.Added, t0);
        repository.RecordFileVersion(s1, @"src\old.py", null, "hash-2", 5, t0, FileChangeKind.Added, t0);
        var t1 = t0.AddDays(1);
        var s2 = repository.BeginSnapshot(t1);
        repository.RecordFileVersion(s2, @"src\old.py", null, "hash-2", 5, t1, FileChangeKind.Deleted, t1);
        var service = new SnapshotHistoryService(repository, new FakeContentStore());

        var withoutDeleted = service.ListDirectory("src", asOf: null, includeDeleted: false);
        Assert.Single(withoutDeleted);

        var withDeleted = service.ListDirectory("src", asOf: null, includeDeleted: true);
        Assert.Equal(2, withDeleted.Count);
        var deletedEntry = Assert.Single(withDeleted, e => e.Name == "old.py");
        Assert.Equal(DirectoryEntryStatus.Deleted, deletedEntry.Status);
    }

    [Fact]
    public void ListDirectory_marks_an_entry_moved_out_of_the_directory_as_moved_not_deleted()
    {
        var repository = new FakeSnapshotRepository();
        var t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var s1 = repository.BeginSnapshot(t0);
        repository.RecordFileVersion(s1, @"src\a.txt", null, "hash-1", 10, t0, FileChangeKind.Added, t0);
        var t1 = t0.AddDays(1);
        var s2 = repository.BeginSnapshot(t1);
        repository.RecordFileVersion(s2, @"src\a.txt", null, "hash-1", 10, t1, FileChangeKind.Deleted, t1);
        repository.RecordFileVersion(s2, @"archive\a.txt", @"src\a.txt", "hash-1", 10, t1, FileChangeKind.Moved, t1);
        var service = new SnapshotHistoryService(repository, new FakeContentStore());

        var entries = service.ListDirectory("src", asOf: null, includeDeleted: true);

        var movedEntry = Assert.Single(entries, e => e.Name == "a.txt");
        Assert.Equal(DirectoryEntryStatus.Moved, movedEntry.Status);
        Assert.Equal(@"archive\a.txt", movedEntry.MovedTo);
    }

    [Fact]
    public void ListDirectory_still_lists_a_wholly_deleted_subdirectory_when_including_deleted_entries()
    {
        var repository = new FakeSnapshotRepository();
        var t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var s1 = repository.BeginSnapshot(t0);
        repository.RecordFileVersion(s1, @"src\gone\a.txt", null, "hash-1", 10, t0, FileChangeKind.Added, t0);
        var t1 = t0.AddDays(1);
        var s2 = repository.BeginSnapshot(t1);
        repository.RecordFileVersion(s2, @"src\gone\a.txt", null, "hash-1", 10, t1, FileChangeKind.Deleted, t1);
        var service = new SnapshotHistoryService(repository, new FakeContentStore());

        Assert.Empty(service.ListDirectory("src", asOf: null, includeDeleted: false));

        var entries = service.ListDirectory("src", asOf: null, includeDeleted: true);
        var dir = Assert.Single(entries, e => e.Name == "gone");
        Assert.Equal(DirectoryEntryKind.Directory, dir.Kind);
        Assert.Equal(DirectoryEntryStatus.Deleted, dir.Status);
    }

    [Fact]
    public void ListDirectory_for_a_never_tracked_directory_throws_NoSuchDirectory()
    {
        var repository = new FakeSnapshotRepository();
        var s1 = repository.BeginSnapshot(DateTimeOffset.UtcNow);
        repository.RecordFileVersion(s1, @"src\a.txt", null, "hash-1", 10, DateTimeOffset.UtcNow, FileChangeKind.Added, DateTimeOffset.UtcNow);
        var service = new SnapshotHistoryService(repository, new FakeContentStore());

        var ex = Assert.Throws<NoSuchDirectoryException>(() => service.ListDirectory("never-tracked-dir", asOf: null, includeDeleted: false));
        Assert.Equal("never-tracked-dir", ex.DirectoryPath);
    }

    [Fact]
    public void ListDeleted_lists_every_currently_deleted_path_across_the_profile()
    {
        var repository = new FakeSnapshotRepository();
        var t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var s1 = repository.BeginSnapshot(t0);
        repository.RecordFileVersion(s1, @"src\a.txt", null, "hash-1", 10, t0, FileChangeKind.Added, t0);
        repository.RecordFileVersion(s1, @"docs\b.txt", null, "hash-2", 5, t0, FileChangeKind.Added, t0);
        var t1 = t0.AddDays(1);
        var s2 = repository.BeginSnapshot(t1);
        repository.RecordFileVersion(s2, @"src\a.txt", null, "hash-1", 10, t1, FileChangeKind.Deleted, t1);
        repository.RecordFileVersion(s2, @"docs\b.txt", null, "hash-2", 5, t1, FileChangeKind.Deleted, t1);
        var service = new SnapshotHistoryService(repository, new FakeContentStore());

        var deleted = service.ListDeleted(directoryPath: null, since: null);

        Assert.Equal(2, deleted.Count);
    }

    [Fact]
    public void ListDeleted_scoped_to_a_subtree_excludes_deletions_outside_it()
    {
        var repository = new FakeSnapshotRepository();
        var t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var s1 = repository.BeginSnapshot(t0);
        repository.RecordFileVersion(s1, @"src\a.txt", null, "hash-1", 10, t0, FileChangeKind.Added, t0);
        repository.RecordFileVersion(s1, @"docs\b.txt", null, "hash-2", 5, t0, FileChangeKind.Added, t0);
        var t1 = t0.AddDays(1);
        var s2 = repository.BeginSnapshot(t1);
        repository.RecordFileVersion(s2, @"src\a.txt", null, "hash-1", 10, t1, FileChangeKind.Deleted, t1);
        repository.RecordFileVersion(s2, @"docs\b.txt", null, "hash-2", 5, t1, FileChangeKind.Deleted, t1);
        var service = new SnapshotHistoryService(repository, new FakeContentStore());

        var deleted = service.ListDeleted(directoryPath: "src", since: null);

        var entry = Assert.Single(deleted);
        Assert.Equal(@"src\a.txt", entry.RelativePath);
    }

    [Fact]
    public void ListDeleted_with_no_deleted_files_returns_an_empty_list()
    {
        var repository = new FakeSnapshotRepository();
        var s1 = repository.BeginSnapshot(DateTimeOffset.UtcNow);
        repository.RecordFileVersion(s1, "a.txt", null, "hash-1", 10, DateTimeOffset.UtcNow, FileChangeKind.Added, DateTimeOffset.UtcNow);
        var service = new SnapshotHistoryService(repository, new FakeContentStore());

        Assert.Empty(service.ListDeleted(directoryPath: null, since: null));
    }

    [Fact]
    public void PlanDirectoryRestore_includes_files_live_at_the_requested_date_and_excludes_files_added_later()
    {
        var repository = new FakeSnapshotRepository();
        var t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var s1 = repository.BeginSnapshot(t0);
        repository.RecordFileVersion(s1, @"src\a.txt", null, "hash-1", 10, t0, FileChangeKind.Added, t0);
        var t1 = t0.AddDays(1);
        var s2 = repository.BeginSnapshot(t1);
        repository.RecordFileVersion(s2, @"src\b.txt", null, "hash-2", 5, t1, FileChangeKind.Added, t1);
        var service = new SnapshotHistoryService(repository, new FakeContentStore());

        var plan = service.PlanDirectoryRestore("src", asOf: t0.AddHours(12), outRoot: @"C:\out", inPlace: false);

        var entry = Assert.Single(plan.ToWrite);
        Assert.Equal(@"src\a.txt", entry.RelativePath);
        Assert.Equal(@"C:\out\a.txt", entry.DestinationPath);
        Assert.Equal(10, plan.TotalBytes);
    }

    [Fact]
    public void PlanDirectoryRestore_includes_a_file_deleted_after_the_requested_date()
    {
        var repository = new FakeSnapshotRepository();
        var t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var s1 = repository.BeginSnapshot(t0);
        repository.RecordFileVersion(s1, @"src\a.txt", null, "hash-1", 10, t0, FileChangeKind.Added, t0);
        var t1 = t0.AddDays(1);
        var s2 = repository.BeginSnapshot(t1);
        repository.RecordFileVersion(s2, @"src\a.txt", null, "hash-1", 10, t1, FileChangeKind.Deleted, t1);
        var service = new SnapshotHistoryService(repository, new FakeContentStore());

        var plan = service.PlanDirectoryRestore("src", asOf: t0.AddHours(12), outRoot: @"C:\out", inPlace: false);

        Assert.Single(plan.ToWrite, e => e.RelativePath == @"src\a.txt");
    }

    [Fact]
    public void PlanDirectoryRestore_excludes_a_tracked_symlink_from_ToWrite_and_reports_it_as_skipped()
    {
        var repository = new FakeSnapshotRepository();
        var t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var s1 = repository.BeginSnapshot(t0);
        repository.RecordFileVersion(s1, @"src\a.txt", null, "hash-1", 10, t0, FileChangeKind.Added, t0);
        repository.RecordFileVersion(s1, @"src\link", null, null, 0, t0, FileChangeKind.Linked, t0, linkTarget: @"C:\target");
        var service = new SnapshotHistoryService(repository, new FakeContentStore());

        var plan = service.PlanDirectoryRestore("src", asOf: null, outRoot: @"C:\out", inPlace: false);

        Assert.Single(plan.ToWrite, e => e.RelativePath == @"src\a.txt");
        Assert.DoesNotContain(plan.ToWrite, e => e.RelativePath == @"src\link");
        Assert.Contains(@"src\link", plan.Skipped);
        Assert.DoesNotContain(@"src\link", plan.ToRemove);
    }

    [Fact]
    public void PlanDirectoryRestore_does_not_remove_a_symlink_that_is_live_at_the_requested_date_even_if_a_stale_destination_file_exists()
    {
        // A symlink/junction live at the requested date contributes nothing to ToWrite
        // (it has no stored content), but it must still count as "live at that date" for
        // ToRemove purposes - otherwise it would look indistinguishable from a path that
        // was never live at all, and get spuriously removed.
        var contentStore = new FakeContentStore();
        var repository = new FakeSnapshotRepository();
        var t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var s1 = repository.BeginSnapshot(t0);
        repository.RecordFileVersion(s1, @"src\link", null, null, 0, t0, FileChangeKind.Linked, t0, linkTarget: @"C:\target");
        contentStore.SeedExistingTarget(@"C:\out\link");
        var service = new SnapshotHistoryService(repository, contentStore);

        var plan = service.PlanDirectoryRestore("src", asOf: t0.AddHours(1), outRoot: @"C:\out", inPlace: false);

        Assert.DoesNotContain(@"C:\out\link", plan.ToRemove);
        Assert.Contains(@"src\link", plan.Skipped);
    }

    [Fact]
    public void PlanDirectoryRestore_computes_removals_for_paths_live_now_but_not_at_the_requested_date()
    {
        var contentStore = new FakeContentStore();
        var repository = new FakeSnapshotRepository();
        var t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var s1 = repository.BeginSnapshot(t0);
        repository.RecordFileVersion(s1, @"src\a.txt", null, "hash-1", 10, t0, FileChangeKind.Added, t0);
        var t1 = t0.AddDays(1);
        var s2 = repository.BeginSnapshot(t1);
        repository.RecordFileVersion(s2, @"src\b.txt", null, "hash-2", 5, t1, FileChangeKind.Added, t1);
        contentStore.SeedExistingTarget(@"C:\out\b.txt");
        var service = new SnapshotHistoryService(repository, contentStore);

        var plan = service.PlanDirectoryRestore("src", asOf: t0.AddHours(12), outRoot: @"C:\out", inPlace: false);

        Assert.Single(plan.ToWrite, e => e.RelativePath == @"src\a.txt");
        Assert.Single(plan.ToRemove, d => d == @"C:\out\b.txt");
    }

    [Fact]
    public void PlanDirectoryRestore_does_not_remove_a_path_not_currently_present_at_its_destination()
    {
        var repository = new FakeSnapshotRepository();
        var t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var s1 = repository.BeginSnapshot(t0);
        repository.RecordFileVersion(s1, @"src\a.txt", null, "hash-1", 10, t0, FileChangeKind.Added, t0);
        var t1 = t0.AddDays(1);
        var s2 = repository.BeginSnapshot(t1);
        repository.RecordFileVersion(s2, @"src\b.txt", null, "hash-2", 5, t1, FileChangeKind.Added, t1);
        // Note: no SeedExistingTarget for b.txt's destination - nothing physically there to remove.
        var service = new SnapshotHistoryService(repository, new FakeContentStore());

        var plan = service.PlanDirectoryRestore("src", asOf: t0.AddHours(12), outRoot: @"C:\out", inPlace: false);

        Assert.Empty(plan.ToRemove);
    }

    [Fact]
    public void PlanDirectoryRestore_in_place_maps_each_entry_to_its_own_original_source_location()
    {
        var repository = new FakeSnapshotRepository();
        var t0 = DateTimeOffset.UtcNow;
        var s1 = repository.BeginSnapshot(t0);
        repository.RecordFileVersion(s1, @"C\Users\john\Documents\a.txt", null, "hash-1", 10, t0, FileChangeKind.Added, t0);
        var service = new SnapshotHistoryService(repository, new FakeContentStore());

        var plan = service.PlanDirectoryRestore(@"C\Users\john\Documents", asOf: null, outRoot: null, inPlace: true);

        var entry = Assert.Single(plan.ToWrite);
        Assert.Equal(@"C:\Users\john\Documents\a.txt", entry.DestinationPath);
    }

    [Fact]
    public void PlanDirectoryRestore_throws_RestoreDestinationInMirror_for_a_write_destination_inside_the_mirror()
    {
        var contentStore = new FakeContentStore();
        contentStore.MirrorPaths.Add(@"C:\out\a.txt");
        var repository = new FakeSnapshotRepository();
        var t0 = DateTimeOffset.UtcNow;
        var s1 = repository.BeginSnapshot(t0);
        repository.RecordFileVersion(s1, @"src\a.txt", null, "hash-1", 10, t0, FileChangeKind.Added, t0);
        var service = new SnapshotHistoryService(repository, contentStore);

        var ex = Assert.Throws<RestoreDestinationInMirrorException>(
            () => service.PlanDirectoryRestore("src", asOf: null, outRoot: @"C:\out", inPlace: false));
        Assert.Equal(@"C:\out\a.txt", ex.DestinationPath);
    }

    [Fact]
    public void PlanDirectoryRestore_for_a_never_tracked_directory_throws_NoSuchDirectory()
    {
        var repository = new FakeSnapshotRepository();
        var s1 = repository.BeginSnapshot(DateTimeOffset.UtcNow);
        repository.RecordFileVersion(s1, @"src\a.txt", null, "hash-1", 10, DateTimeOffset.UtcNow, FileChangeKind.Added, DateTimeOffset.UtcNow);
        var service = new SnapshotHistoryService(repository, new FakeContentStore());

        var ex = Assert.Throws<NoSuchDirectoryException>(
            () => service.PlanDirectoryRestore("never-tracked-dir", asOf: null, outRoot: @"C:\out", inPlace: false));
        Assert.Equal("never-tracked-dir", ex.DirectoryPath);
    }

    [Fact]
    public void ExecuteDirectoryRestore_writes_planned_entries_and_removes_planned_destinations()
    {
        var contentStore = new FakeContentStore();
        var (hashA, sizeA) = contentStore.StoreFromStream(new MemoryStream("content a"u8.ToArray()));
        var (hashC, sizeC) = contentStore.StoreFromStream(new MemoryStream("content c"u8.ToArray()));
        var tempRoot = Path.Combine(Path.GetTempPath(), $"vara-dirrestore-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        var destinationA = Path.Combine(tempRoot, "a.txt");
        var destinationC = Path.Combine(tempRoot, "sub", "c.txt");
        var toRemove = Path.Combine(tempRoot, "stale.txt");
        File.WriteAllText(toRemove, "should be removed");

        try
        {
            var plan = new DirectoryRestorePlan(
                ToWrite:
                [
                    new DirectoryRestoreEntry(@"src\a.txt", hashA, sizeA, destinationA),
                    new DirectoryRestoreEntry(@"src\sub\c.txt", hashC, sizeC, destinationC),
                ],
                ToRemove: [toRemove],
                TotalBytes: sizeA + sizeC,
                Skipped: []);
            var service = new SnapshotHistoryService(new FakeSnapshotRepository(), contentStore);

            var chunkReports = new List<long>();
            long? resolvedSize = null;

            service.ExecuteDirectoryRestore(
                plan,
                onBytesCopied: bytes => chunkReports.Add(bytes),
                onSizeResolved: resolved => resolvedSize = resolved);

            Assert.Equal("content a", File.ReadAllText(destinationA));
            Assert.Equal("content c", File.ReadAllText(destinationC));
            Assert.False(File.Exists(toRemove));
            Assert.Equal(plan.TotalBytes, resolvedSize);
            Assert.Equal(plan.TotalBytes, chunkReports.Sum());
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void ExecuteDirectoryRestore_performs_no_io_for_a_skipped_entry_and_does_not_throw()
    {
        var contentStore = new FakeContentStore();
        var (hashA, sizeA) = contentStore.StoreFromStream(new MemoryStream("content a"u8.ToArray()));
        var tempRoot = Path.Combine(Path.GetTempPath(), $"vara-dirrestore-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        var destinationA = Path.Combine(tempRoot, "a.txt");
        var linkDestination = Path.Combine(tempRoot, "link");

        try
        {
            var plan = new DirectoryRestorePlan(
                ToWrite: [new DirectoryRestoreEntry(@"src\a.txt", hashA, sizeA, destinationA)],
                ToRemove: [],
                TotalBytes: sizeA,
                Skipped: [@"src\link"]);
            var service = new SnapshotHistoryService(new FakeSnapshotRepository(), contentStore);

            service.ExecuteDirectoryRestore(plan);

            Assert.Equal("content a", File.ReadAllText(destinationA));
            Assert.False(File.Exists(linkDestination));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }
}

