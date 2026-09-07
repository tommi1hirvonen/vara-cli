using Microsoft.Data.Sqlite;
using Vara.Application.Backup;
using Vara.Application.Profiles;
using Vara.Cli.Commands;
using Vara.Cli.Composition;
using Vara.Cli.Presentation;
using Vara.Core.Abstractions;
using Vara.Core.Configuration;
using Vara.Core.Snapshots;
using Vara.Infrastructure.Hashing;
using Xunit;

namespace Vara.Cli.Tests.Commands;

public class BackupCommandTests
{
    private static BackupRunResult MakeResult(IReadOnlyList<string> failedPaths)
    {
        var startedAt = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var stats = SnapshotStats.Empty with { FilesFailed = failedPaths.Count };
        return new BackupRunResult(1, startedAt, startedAt.AddSeconds(1), stats, failedPaths);
    }

    [Fact]
    public void ResolveOutcome_is_success_when_no_files_failed()
    {
        var result = MakeResult([]);

        Assert.Equal(BackupProgressOutcome.Success, BackupCommand.ResolveOutcome(result));
    }

    [Fact]
    public void ResolveOutcome_is_partial_failure_when_one_or_more_files_failed()
    {
        var result = MakeResult(["some/failed/path.txt"]);

        Assert.Equal(BackupProgressOutcome.PartialFailure, BackupCommand.ResolveOutcome(result));
    }
}

/// <summary>
/// Exercises <see cref="BackupCommand"/>'s wiring end-to-end (via <c>SetAction</c>), against a
/// real profile-scoped repository/content store (through the real
/// <see cref="ProfileServiceFactory"/>) but a fake <see cref="IFileSystemScanner"/>, whose
/// reported entries/failures are test-controlled - covers this change's "exit code reflects
/// FailedPaths" wiring (tasks.md 2.3/2.4) without needing to reproduce a real locked/denied
/// file on disk.
/// </summary>
public class BackupCommandExitCodeTests : IDisposable
{
    private readonly string _targetRoot = Path.Combine(Path.GetTempPath(), $"vara-clitest-{Guid.NewGuid():N}");
    private readonly string _sourceRoot = Path.Combine(Path.GetTempPath(), $"vara-clitest-src-{Guid.NewGuid():N}");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_targetRoot))
        {
            Directory.Delete(_targetRoot, recursive: true);
        }
    }

    private sealed class SingleProfileConfigLoader(Profile profile) : IProfileConfigLoader
    {
        public IReadOnlyList<Profile> LoadProfiles(string configPath) => [profile];
        public string DefaultConfigPath => "unused";
    }

    /// <summary>Fake scanner returning a pre-set, test-controlled list of entries (and,
    /// optionally, scan failures) regardless of the sources argument - copied from the
    /// equivalent fake in <c>Vara.Application.Tests</c>/<c>Vara.IntegrationTests</c> per this
    /// project's convention of duplicating small, drift-risk-free test fakes rather than
    /// referencing another test project.</summary>
    private sealed class FakeFileSystemScanner(IReadOnlyList<ScanFailure>? failures = null) : IFileSystemScanner
    {
        public ScanResult Scan(IReadOnlyList<Source> sources) => new([], failures ?? []);
    }

    private System.CommandLine.Command CreateCommand(IFileSystemScanner scanner)
    {
        var profile = new Profile("test-profile", _targetRoot, [new Source(_sourceRoot)], null);
        var hasher = new XxHash128Hasher();
        return BackupCommand.Create(
            new ProfileResolver(new SingleProfileConfigLoader(profile)),
            new ProfileServiceFactory(hasher),
            scanner,
            hasher);
    }

    [Fact]
    public void A_run_with_no_failed_paths_returns_exit_code_success()
    {
        var command = CreateCommand(new FakeFileSystemScanner());

        var exitCode = command.Parse(["--profile", "test-profile"]).Invoke();

        Assert.Equal(ExitCodes.Success, exitCode);
    }

    [Fact]
    public void A_run_with_one_or_more_failed_paths_returns_exit_code_partial_failure()
    {
        var failure = new ScanFailure(@"D:\missing-drive\app-data", ScanFailureReason.SourceUnavailable, "app-data");
        var command = CreateCommand(new FakeFileSystemScanner([failure]));

        var exitCode = command.Parse(["--profile", "test-profile"]).Invoke();

        Assert.Equal(ExitCodes.PartialFailure, exitCode);
    }

    [Fact]
    public void A_hard_error_before_the_run_completes_returns_exit_code_hard_error_not_partial_failure()
    {
        var command = CreateCommand(new FakeFileSystemScanner());

        // Requests a profile name the loader doesn't know about, so ProfileResolver.Resolve
        // throws UnknownProfileException before `result` is ever assigned - guards against the
        // partial-failure override in BackupCommand firing on an unassigned/default result.
        var exitCode = command.Parse(["--profile", "no-such-profile"]).Invoke();

        Assert.Equal(ExitCodes.HardError, exitCode);
    }
}
