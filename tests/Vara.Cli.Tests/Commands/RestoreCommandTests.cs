using Vara.Application.Profiles;
using Vara.Cli.Commands;
using Vara.Cli.Composition;
using Vara.Core.Abstractions;
using Vara.Core.Configuration;
using Vara.Core.Snapshots;
using Xunit;

namespace Vara.Cli.Tests.Commands;

public class RestoreCommandTests
{
    // Neither dependency is ever exercised by the tests below: --recursive/--version and
    // --at/--version mutual exclusion (like --out/--in-place's existing check) is validated
    // by RestoreCommand.Create's parsed-args handler before profile resolution or service
    // construction is ever reached, so a real profile/hasher is unnecessary here.
    private sealed class UnusedProfileConfigLoader : IProfileConfigLoader
    {
        public IReadOnlyList<Profile> LoadProfiles(string configPath) => throw new NotSupportedException();
        public string DefaultConfigPath => throw new NotSupportedException();
    }

    private sealed class UnusedHasher : IHasher
    {
        public string ComputeHash(Stream content) => throw new NotSupportedException();
    }

    private static System.CommandLine.Command CreateCommand() =>
        RestoreCommand.Create(new ProfileResolver(new UnusedProfileConfigLoader()), new ProfileServiceFactory(new UnusedHasher()));

    [Fact]
    public void Recursive_combined_with_version_is_rejected_before_any_profile_resolution()
    {
        var command = CreateCommand();

        var exitCode = command.Parse(["src", "--recursive", "--version", "5", "--out", @"C:\out"]).Invoke();

        Assert.Equal(1, exitCode);
    }

    [Fact]
    public void Recursive_without_version_and_without_out_or_in_place_still_requires_a_destination()
    {
        var command = CreateCommand();

        // --recursive alone doesn't bypass the pre-existing --out/--in-place requirement.
        var exitCode = command.Parse(["src", "--recursive"]).Invoke();

        Assert.Equal(1, exitCode);
    }

    [Fact]
    public void At_combined_with_version_is_rejected_before_any_profile_resolution()
    {
        var command = CreateCommand();

        var exitCode = command.Parse(["src", "--at", "2025-01-15", "--version", "5", "--out", @"C:\out"]).Invoke();

        Assert.Equal(1, exitCode);
    }

    [Fact]
    public void SelectableVersionsForInteractivePicker_excludes_deleted_and_linked_versions()
    {
        var t0 = DateTimeOffset.UtcNow;
        var added = new FileVersionRecord(1, 1, "a.txt", null, "hash-1", 5, t0, FileChangeKind.Added, t0);
        var deleted = new FileVersionRecord(2, 2, "a.txt", null, "hash-1", 5, t0.AddMinutes(1), FileChangeKind.Deleted, t0.AddMinutes(1));
        var linked = new FileVersionRecord(3, 3, "a.txt", null, null, 0, t0.AddMinutes(2), FileChangeKind.Linked, t0.AddMinutes(2), LinkTarget: @"C:\target");
        var history = new List<FileVersionRecord> { linked, deleted, added };

        var selectable = RestoreCommand.SelectableVersionsForInteractivePicker(history);

        Assert.Single(selectable);
        Assert.Equal(added, selectable[0]);
    }

    [Fact]
    public void SelectableVersionsForInteractivePicker_is_empty_when_every_version_is_deleted_or_linked()
    {
        var t0 = DateTimeOffset.UtcNow;
        var deleted = new FileVersionRecord(1, 1, "link", null, "hash-1", 5, t0, FileChangeKind.Deleted, t0);
        var linked = new FileVersionRecord(2, 2, "link", null, null, 0, t0.AddMinutes(1), FileChangeKind.Linked, t0.AddMinutes(1), LinkTarget: @"C:\target");
        var history = new List<FileVersionRecord> { linked, deleted };

        var selectable = RestoreCommand.SelectableVersionsForInteractivePicker(history);

        Assert.Empty(selectable);
    }
}
