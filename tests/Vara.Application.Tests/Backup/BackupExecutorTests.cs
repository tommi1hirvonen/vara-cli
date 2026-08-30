using Vara.Application.Backup;
using Xunit;

namespace Vara.Application.Tests.Backup;

public class BackupExecutorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"vara-test-{Guid.NewGuid():N}");
    private readonly FakeContentStore _contentStore = new();
    private readonly FakeSnapshotRepository _repository = new();

    public BackupExecutorTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private string WriteFile(string name, string content)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void Executing_an_add_stores_content_places_it_in_the_mirror_and_records_it()
    {
        var path = WriteFile("new.txt", "hello world");
        var plan = new BackupPlan(
            [new PlannedOperation(PlannedOperationKind.Add, "new.txt", null, path, 11, DateTimeOffset.UtcNow, null)], 11);
        var executor = new BackupExecutor(_contentStore, _repository);
        var snapshotId = _repository.BeginSnapshot(DateTimeOffset.UtcNow);

        var outcome = executor.Execute(snapshotId, DateTimeOffset.UtcNow, plan);

        Assert.Equal(1, outcome.FilesAdded);
        Assert.Equal(11, outcome.BytesTransferred);
        Assert.Empty(outcome.FailedPaths);
        Assert.True(_contentStore.Mirror.ContainsKey("new.txt"));
    }

    [Fact]
    public void A_locked_source_file_is_recorded_as_failed_without_aborting_the_run()
    {
        var lockedPath = WriteFile("locked.txt", "cannot read me");
        var okPath = WriteFile("ok.txt", "this one is fine");
        using var exclusiveHold = new FileStream(lockedPath, FileMode.Open, FileAccess.Read, FileShare.None);

        var plan = new BackupPlan(
            [
                new PlannedOperation(PlannedOperationKind.Add, "locked.txt", null, lockedPath, 15, DateTimeOffset.UtcNow, null),
                new PlannedOperation(PlannedOperationKind.Add, "ok.txt", null, okPath, 17, DateTimeOffset.UtcNow, null),
            ],
            32);
        var executor = new BackupExecutor(_contentStore, _repository);
        var snapshotId = _repository.BeginSnapshot(DateTimeOffset.UtcNow);

        var outcome = executor.Execute(snapshotId, DateTimeOffset.UtcNow, plan);

        Assert.Equal(1, outcome.FilesFailed);
        Assert.Equal(["locked.txt"], outcome.FailedPaths);
        Assert.Equal(1, outcome.FilesAdded);
        Assert.True(_contentStore.Mirror.ContainsKey("ok.txt"));
        Assert.False(_contentStore.Mirror.ContainsKey("locked.txt"));
    }

    [Fact]
    public void Executing_a_move_relocates_the_mirror_entry_and_records_delete_plus_move()
    {
        _contentStore.PlaceAtMirrorPath("hash-1", @"Downloads\report.pdf");
        var plan = new BackupPlan(
            [new PlannedOperation(PlannedOperationKind.Move, @"Documents\report.pdf", @"Downloads\report.pdf", null, 100, DateTimeOffset.UtcNow, "hash-1")],
            0);
        var executor = new BackupExecutor(_contentStore, _repository);
        var snapshotId = _repository.BeginSnapshot(DateTimeOffset.UtcNow);

        var outcome = executor.Execute(snapshotId, DateTimeOffset.UtcNow, plan);

        Assert.Equal(1, outcome.FilesMoved);
        Assert.Equal(0, outcome.BytesTransferred);
        Assert.False(_contentStore.Mirror.ContainsKey(@"Downloads\report.pdf"));
        Assert.True(_contentStore.Mirror.ContainsKey(@"Documents\report.pdf"));

        var history = _repository.GetFileHistory(@"Downloads\report.pdf");
        Assert.Contains(history, r => r.ChangeKind == Vara.Core.Snapshots.FileChangeKind.Deleted);
    }

    [Fact]
    public void Executing_a_delete_removes_the_mirror_entry_and_records_a_tombstone()
    {
        _contentStore.PlaceAtMirrorPath("hash-1", "gone.txt");
        var plan = new BackupPlan([new PlannedOperation(PlannedOperationKind.Delete, "gone.txt", null, null, 10, DateTimeOffset.UtcNow, "hash-1")], 0);
        var executor = new BackupExecutor(_contentStore, _repository);
        var snapshotId = _repository.BeginSnapshot(DateTimeOffset.UtcNow);

        var outcome = executor.Execute(snapshotId, DateTimeOffset.UtcNow, plan);

        Assert.Equal(1, outcome.FilesDeleted);
        Assert.False(_contentStore.Mirror.ContainsKey("gone.txt"));
    }

    [Fact]
    public void Many_adds_execute_correctly_under_bounded_parallelism()
    {
        const int fileCount = 50;
        var operations = new List<PlannedOperation>();
        long expectedBytes = 0;

        for (var i = 0; i < fileCount; i++)
        {
            var content = $"content of file {i}";
            var path = WriteFile($"file-{i}.txt", content);
            var size = new FileInfo(path).Length;
            expectedBytes += size;
            operations.Add(new PlannedOperation(PlannedOperationKind.Add, $"file-{i}.txt", null, path, size, DateTimeOffset.UtcNow, null));
        }

        var plan = new BackupPlan(operations, expectedBytes);
        var executor = new BackupExecutor(_contentStore, _repository, maxDegreeOfParallelism: 8);
        var snapshotId = _repository.BeginSnapshot(DateTimeOffset.UtcNow);

        var reportedBytes = 0L;
        var outcome = executor.Execute(snapshotId, DateTimeOffset.UtcNow, plan, bytes => Interlocked.Add(ref reportedBytes, bytes));

        Assert.Equal(fileCount, outcome.FilesAdded);
        Assert.Equal(expectedBytes, outcome.BytesTransferred);
        Assert.Equal(expectedBytes, reportedBytes);
        Assert.Empty(outcome.FailedPaths);
        Assert.Equal(fileCount, _contentStore.Mirror.Count);
        Assert.Equal(fileCount, _repository.GetCurrentState().Count);
    }
}
