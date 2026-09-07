using Microsoft.Data.Sqlite;
using Vara.Application.Backup;
using Vara.Core.Backup;
using Vara.Infrastructure.Snapshots;
using Xunit;

namespace Vara.IntegrationTests.Backup;

/// <summary>
/// Verifies periodic-checkpointing's bounded redo window (add-backup-checkpoints-and-cancellation's
/// design.md) against the real <see cref="SqliteSnapshotRepository"/>: an interruption
/// should only discard the manifest rows recorded since the most recent checkpoint, not
/// everything recorded since the run began - unlike the single whole-run commit this
/// executor used before. <see cref="IContentStore"/>/<see cref="IHasher"/> remain faked
/// here, per the same rationale as <see cref="BackupPipelineRealRepositoryTests"/> -
/// this behavior is entirely about the manifest's transaction boundaries.
/// </summary>
public class BackupExecutorCheckpointRealRepositoryTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"vara-inttest-{Guid.NewGuid():N}.db");
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"vara-inttest-{Guid.NewGuid():N}");

    public BackupExecutorCheckpointRealRepositoryTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath))
        {
            File.Delete(_dbPath);
        }

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
    public void An_interruption_between_checkpoints_only_undoes_work_recorded_after_the_last_completed_checkpoint()
    {
        const int checkpointedCount = 4;
        const int unfinishedCount = 3;
        var allOperations = new List<PlannedOperation>();

        for (var i = 0; i < checkpointedCount + unfinishedCount; i++)
        {
            var path = WriteFile($"file-{i}.txt", $"content of file {i}");
            var size = new FileInfo(path).Length;
            allOperations.Add(new PlannedOperation(PlannedOperationKind.Add, $"file-{i}.txt", null, path, size, new FileInfo(path).LastWriteTimeUtc, null));
        }

        var contentStore = new FakeContentStore();
        var hasher = new FakeHasher();

        using (var repository = new SqliteSnapshotRepository(_dbPath))
        {
            var snapshotId = repository.BeginSnapshot(DateTimeOffset.UtcNow);
            using var batch = repository.BeginManifestBatch();
            var executor = new BackupExecutor(contentStore, repository, hasher, maxDegreeOfParallelism: 1);

            // First checkpointedCount files: recorded, then explicitly checkpointed -
            // these must survive the "crash" below, exactly as a real periodic
            // checkpoint firing at this point would guarantee.
            var checkpointedPlan = new BackupPlan(allOperations.Take(checkpointedCount).ToList(), 0);
            executor.Execute(snapshotId, DateTimeOffset.UtcNow, checkpointedPlan);
            batch.Commit();

            // Remaining files: recorded but never checkpointed - models a crash before
            // the next periodic checkpoint (or the run's own final commit) occurs.
            var unfinishedPlan = new BackupPlan(allOperations.Skip(checkpointedCount).ToList(), 0);
            executor.Execute(snapshotId, DateTimeOffset.UtcNow, unfinishedPlan);
            // No further Commit() - disposing the batch here rolls back only these
            // unfinished rows, not the ones the earlier checkpoint already committed.
        }

        // A fresh repository instance opening the same file models the process restarting.
        using var reopened = new SqliteSnapshotRepository(_dbPath);
        reopened.ReconcileIncompleteSnapshots();

        var currentState = reopened.GetCurrentState();
        Assert.Equal(checkpointedCount, currentState.Count);
        for (var i = 0; i < checkpointedCount; i++)
        {
            Assert.True(currentState.ContainsKey($"file-{i}.txt"), $"file-{i}.txt should have survived (checkpointed).");
        }

        for (var i = checkpointedCount; i < checkpointedCount + unfinishedCount; i++)
        {
            Assert.False(currentState.ContainsKey($"file-{i}.txt"), $"file-{i}.txt should have been discarded (never checkpointed).");
        }
    }
}
