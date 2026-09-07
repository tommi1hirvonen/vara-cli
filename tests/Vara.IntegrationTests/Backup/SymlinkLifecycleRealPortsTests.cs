using Microsoft.Data.Sqlite;
using Vara.Application.Backup;
using Vara.Application.History;
using Vara.Application.Integrity;
using Vara.Core.Abstractions;
using Vara.Core.Configuration;
using Vara.Infrastructure.Hashing;
using Vara.Infrastructure.Snapshots;
using Vara.Infrastructure.Storage;
using Vara.IntegrationTests.TestSupport;
using Xunit;

namespace Vara.IntegrationTests.Backup;

/// <summary>
/// Exercises the full symlink lifecycle - backup, integrity check, and recursive restore -
/// against the real <see cref="SqliteSnapshotRepository"/> and real
/// <see cref="FileSystemContentStore"/> (not fakes), per the separate-link-target-storage
/// change: a profile containing a tracked symlink/junction, and a regular file later replaced
/// by one, must never produce a false "missing blob" from <c>vara check</c>, never cause a
/// partial <c>restore --recursive</c>, and never leave a stale mirror copy behind after a
/// file-to-link transition.
/// </summary>
public class SymlinkLifecycleRealPortsTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"vara-inttest-{Guid.NewGuid():N}.db");
    private readonly string _sourceRoot = Path.Combine(Path.GetTempPath(), $"vara-inttest-src-{Guid.NewGuid():N}");
    private readonly string _targetRoot = Path.Combine(Path.GetTempPath(), $"vara-inttest-tgt-{Guid.NewGuid():N}");
    private SqliteSnapshotRepository? _repository;
    private FileSystemContentStore? _contentStore;

    private SqliteSnapshotRepository Repository => _repository ??= new SqliteSnapshotRepository(_dbPath);
    private FileSystemContentStore ContentStore => _contentStore ??= new FileSystemContentStore(_targetRoot, new XxHash128Hasher());

    public SymlinkLifecycleRealPortsTests() => Directory.CreateDirectory(_sourceRoot);

    public void Dispose()
    {
        _repository?.Dispose();
        SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath))
        {
            File.Delete(_dbPath);
        }

        if (Directory.Exists(_sourceRoot))
        {
            Directory.Delete(_sourceRoot, recursive: true);
        }

        DirectoryCleanup.ClearReadOnlyAndDelete(_targetRoot);
    }

    private static Profile SimpleProfile(string sourceRoot, string targetRoot) =>
        new("files", targetRoot, [new Source(sourceRoot)], null, null);

    [Fact]
    public void Backup_check_and_recursive_restore_handle_a_tracked_symlink_and_a_file_to_link_transition_correctly()
    {
        var regularPath = Path.Combine(_sourceRoot, "regular.txt");
        File.WriteAllText(regularPath, "hello");
        var regularEntry = new ScannedEntry("regular.txt", regularPath, 5, File.GetLastWriteTimeUtc(regularPath), false, null);

        var staleFilePath = Path.Combine(_sourceRoot, "stale.txt");
        File.WriteAllText(staleFilePath, "will become a link");
        var staleFileEntry = new ScannedEntry("stale.txt", staleFilePath, new FileInfo(staleFilePath).Length, File.GetLastWriteTimeUtc(staleFilePath), false, null);

        var linkPath = Path.Combine(_sourceRoot, "link");
        var linkEntry = new ScannedEntry("link", linkPath, 0, DateTimeOffset.MinValue, true, @"C:\somewhere\else.txt");

        var hasher = new XxHash128Hasher();

        // First run: two regular files and a tracked symlink.
        var firstRun = new BackupPipeline(
                new FakeFileSystemScanner([regularEntry, staleFileEntry, linkEntry]), hasher, ContentStore, Repository, new FakeRunLock())
            .Run(SimpleProfile(_sourceRoot, _targetRoot));

        Assert.Equal(2, firstRun.Stats.FilesAdded);
        Assert.Empty(firstRun.FailedPaths);
        Assert.True(File.Exists(Path.Combine(_targetRoot, "stale.txt")));

        // vara check: the tracked symlink must not be reported as a missing blob.
        var checkService = new IntegrityCheckService(Repository, ContentStore, hasher);
        var firstCheck = checkService.Check(quick: false);
        Assert.Empty(firstCheck.Missing);
        Assert.Empty(firstCheck.Corrupt);

        // Second run: "stale.txt" is now replaced by a symlink at the same path - a
        // file-to-link transition. The regular file and the pre-existing link are unchanged.
        var staleBecomesLinkEntry = new ScannedEntry("stale.txt", staleFilePath, 0, DateTimeOffset.MinValue, true, @"C:\somewhere\else.txt");
        var secondRun = new BackupPipeline(
                new FakeFileSystemScanner([regularEntry, staleBecomesLinkEntry, linkEntry]), hasher, ContentStore, Repository, new FakeRunLock())
            .Run(SimpleProfile(_sourceRoot, _targetRoot));

        Assert.Equal(0, secondRun.Stats.FilesAdded);
        Assert.Equal(0, secondRun.Stats.FilesChanged);
        Assert.Equal(0, secondRun.Stats.FilesDeleted);
        Assert.Empty(secondRun.FailedPaths);

        var currentState = Repository.GetCurrentState();
        Assert.True(currentState["stale.txt"].IsLinked);
        Assert.Null(currentState["stale.txt"].ContentHash);

        // The stale mirror copy left over from when "stale.txt" was a regular file must be
        // gone now that the manifest considers it a link.
        Assert.False(File.Exists(Path.Combine(_targetRoot, "stale.txt")));

        // vara check again: still no false findings for either link.
        var secondCheck = checkService.Check(quick: false);
        Assert.Empty(secondCheck.Missing);
        Assert.Empty(secondCheck.Corrupt);

        // restore --recursive of the whole profile: the regular file restores normally, and
        // both links are skipped (not written, not treated as a failure) rather than causing
        // a partial restore.
        var history = new SnapshotHistoryService(Repository, ContentStore);
        var outRoot = Path.Combine(Path.GetTempPath(), $"vara-inttest-restore-{Guid.NewGuid():N}");

        try
        {
            var plan = history.PlanDirectoryRestore(string.Empty, asOf: null, outRoot, inPlace: false);

            Assert.Single(plan.ToWrite, e => e.RelativePath == "regular.txt");
            Assert.DoesNotContain(plan.ToWrite, e => e.RelativePath == "stale.txt");
            Assert.DoesNotContain(plan.ToWrite, e => e.RelativePath == "link");
            Assert.Contains("stale.txt", plan.Skipped);
            Assert.Contains("link", plan.Skipped);

            history.ExecuteDirectoryRestore(plan);

            Assert.Equal("hello", File.ReadAllText(Path.Combine(outRoot, "regular.txt")));
            Assert.False(File.Exists(Path.Combine(outRoot, "stale.txt")));
            Assert.False(File.Exists(Path.Combine(outRoot, "link")));
        }
        finally
        {
            if (Directory.Exists(outRoot))
            {
                Directory.Delete(outRoot, recursive: true);
            }
        }
    }
}
