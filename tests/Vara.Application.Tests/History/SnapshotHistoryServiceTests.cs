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
    public void RestoreVersion_with_an_unknown_id_throws_NoMatchingVersion()
    {
        var repository = new FakeSnapshotRepository();
        var s1 = repository.BeginSnapshot(DateTimeOffset.UtcNow);
        repository.RecordFileVersion(s1, "a.txt", null, "hash", 10, DateTimeOffset.UtcNow, FileChangeKind.Added, DateTimeOffset.UtcNow);
        var service = new SnapshotHistoryService(repository, new FakeContentStore());

        Assert.Throws<NoMatchingVersionException>(() => service.RestoreVersion("a.txt", 9999, "out.txt"));
    }
}
