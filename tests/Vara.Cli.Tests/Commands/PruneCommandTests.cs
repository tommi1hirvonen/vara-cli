using Microsoft.Data.Sqlite;
using Vara.Application.Profiles;
using Vara.Cli.Commands;
using Vara.Cli.Composition;
using Vara.Core.Abstractions;
using Vara.Core.Configuration;
using Vara.Core.Snapshots;
using Vara.Infrastructure.Hashing;
using Vara.Infrastructure.Snapshots;
using Xunit;

namespace Vara.Cli.Tests.Commands;

/// <summary>
/// Exercises <see cref="PruneCommand"/>'s wiring against a real profile-scoped repository
/// (via the real <see cref="ProfileServiceFactory"/>), covering the paths reachable without
/// an interactive console: the confirmation prompt itself (<c>StandardError.Console.Confirm</c>)
/// is not injectable, and this test host's <c>Console.IsInputRedirected</c> is always
/// <see langword="true"/> (as in CI), so the interactive-confirm branch can't be driven from
/// an in-process test here - that scenario (the confirmed snapshot id list being honored, even
/// as the live-eligible set changes concurrently) is instead covered directly at the
/// <c>PruneService</c> level (see <c>PruneServiceTests</c>'s <c>Prune_with_confirmed_ids_*</c>
/// cases), and manually per this change's tasks.md.
/// </summary>
public class PruneCommandTests : IDisposable
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

    private System.CommandLine.Command CreateCommand(RetentionPolicy? retention)
    {
        var profile = new Profile("test-profile", _targetRoot, [new Source(_sourceRoot)], retention);
        return PruneCommand.Create(
            new ProfileResolver(new SingleProfileConfigLoader(profile)),
            new ProfileServiceFactory(new XxHash128Hasher()));
    }

    /// <summary>Seeds three completed snapshots directly into the profile's manifest, of
    /// which the two oldest are eligible for removal under a keepDaily:1 policy.</summary>
    private void SeedThreeSnapshotsTwoEligible()
    {
        var dbPath = Path.Combine(_targetRoot, ".vara", "profile.db");
        using var repository = new SqliteSnapshotRepository(dbPath);

        var day1 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var day2 = day1.AddDays(1);
        var day3 = day1.AddDays(2);

        var oldSnapshot = repository.BeginSnapshot(day1);
        repository.RecordFileVersion(oldSnapshot, "a.txt", null, "hash-1", 10, day1, FileChangeKind.Added, day1);
        repository.CompleteSnapshot(oldSnapshot, day1, SnapshotStats.Empty);

        var middleSnapshot = repository.BeginSnapshot(day2);
        repository.RecordFileVersion(middleSnapshot, "a.txt", null, "hash-2", 10, day2, FileChangeKind.Changed, day2);
        repository.CompleteSnapshot(middleSnapshot, day2, SnapshotStats.Empty);

        var newestSnapshot = repository.BeginSnapshot(day3);
        repository.RecordFileVersion(newestSnapshot, "a.txt", null, "hash-3", 10, day3, FileChangeKind.Changed, day3);
        repository.CompleteSnapshot(newestSnapshot, day3, SnapshotStats.Empty);
    }

    private IReadOnlyList<long> CurrentSnapshotIds()
    {
        var dbPath = Path.Combine(_targetRoot, ".vara", "profile.db");
        using var repository = new SqliteSnapshotRepository(dbPath, createIfMissing: false);
        return repository.ListSnapshots().Select(s => s.Id).ToList();
    }

    [Fact]
    public void Yes_option_prunes_the_full_live_eligible_set_without_prompting()
    {
        SeedThreeSnapshotsTwoEligible();
        var command = CreateCommand(new RetentionPolicy(keepDaily: 1, keepWeekly: 0, keepMonthly: 0, keepYearly: 0));

        var exitCode = command.Parse(["--profile", "test-profile", "--yes"]).Invoke();

        Assert.Equal(0, exitCode);
        Assert.Single(CurrentSnapshotIds());
    }

    [Fact]
    public void Zero_eligible_snapshots_prunes_without_prompting_and_removes_nothing()
    {
        var dbPath = Path.Combine(_targetRoot, ".vara", "profile.db");
        using (var repository = new SqliteSnapshotRepository(dbPath))
        {
            var day1 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
            var onlySnapshot = repository.BeginSnapshot(day1);
            repository.RecordFileVersion(onlySnapshot, "a.txt", null, "hash-1", 10, day1, FileChangeKind.Added, day1);
            repository.CompleteSnapshot(onlySnapshot, day1, SnapshotStats.Empty);
        }

        var command = CreateCommand(new RetentionPolicy(keepDaily: 1, keepWeekly: 0, keepMonthly: 0, keepYearly: 0));

        // No --yes given, and only one (always-retained) snapshot exists, so nothing is
        // eligible: this must proceed without hitting the confirmation-required error.
        var exitCode = command.Parse(["--profile", "test-profile"]).Invoke();

        Assert.Equal(0, exitCode);
        Assert.Single(CurrentSnapshotIds());
    }

    [Fact]
    public void Eligible_snapshots_without_yes_in_a_non_interactive_session_fails_without_removing_anything()
    {
        SeedThreeSnapshotsTwoEligible();
        var command = CreateCommand(new RetentionPolicy(keepDaily: 1, keepWeekly: 0, keepMonthly: 0, keepYearly: 0));

        // This test host's stdin is redirected (as in CI), so the command takes its
        // non-interactive path: it must refuse rather than silently prune or hang.
        var exitCode = command.Parse(["--profile", "test-profile"]).Invoke();

        Assert.Equal(1, exitCode);
        Assert.Equal(3, CurrentSnapshotIds().Count);
    }
}
