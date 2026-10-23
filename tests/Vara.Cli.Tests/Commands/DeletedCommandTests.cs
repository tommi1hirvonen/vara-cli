using Spectre.Console.Testing;
using Vara.Cli.Commands;
using Vara.Core.Snapshots;
using Xunit;

namespace Vara.Cli.Tests.Commands;

public class DeletedCommandTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2025, 1, 10, 12, 0, 0, TimeSpan.Zero);

    private readonly ProfileFixture _fixture = new();
    private readonly TestConsole _console = new();
    private readonly TestConsole _errors = new();

    public void Dispose() => _fixture.Dispose();

    private int Run(params string[] args) =>
        DeletedCommand.Create(_fixture.Resolver, _fixture.Services, _console, _errors)
            .Parse([.. args, "--profile", "test-profile"]).Invoke();

    private void SeedDeletion(string path, DateTimeOffset addedAt, DateTimeOffset deletedAt)
    {
        _fixture.Record(path, "content", FileChangeKind.Added, addedAt);
        _fixture.Record(path, "content", FileChangeKind.Deleted, deletedAt);
    }

    [Fact]
    public void Reports_deleted_files_and_leaves_live_ones_out()
    {
        _fixture.Record("live.txt", "x", FileChangeKind.Added, T0);
        SeedDeletion("gone.txt", T0, T0.AddDays(1));

        var exitCode = Run();

        Assert.Equal(0, exitCode);
        Assert.Contains("gone.txt", _console.Output);
        Assert.DoesNotContain("live.txt", _console.Output);
    }

    [Fact]
    public void Since_excludes_files_deleted_before_the_given_time()
    {
        SeedDeletion("old.txt", T0, T0.AddDays(1));
        SeedDeletion("recent.txt", T0, T0.AddDays(20));

        var exitCode = Run("--since", T0.AddDays(10).ToString("yyyy-MM-dd"));

        Assert.Equal(0, exitCode);
        Assert.Contains("recent.txt", _console.Output);
        Assert.DoesNotContain("old.txt", _console.Output);
    }

    [Fact]
    public void Directory_argument_scopes_the_report_to_that_directory()
    {
        SeedDeletion(@"src\inside.txt", T0, T0.AddDays(1));
        SeedDeletion(@"other\outside.txt", T0, T0.AddDays(1));

        var exitCode = Run("src");

        Assert.Equal(0, exitCode);
        Assert.Contains("inside.txt", _console.Output);
        Assert.DoesNotContain("outside.txt", _console.Output);
    }

    [Fact]
    public void With_no_deletions_says_so()
    {
        _fixture.Record("live.txt", "x", FileChangeKind.Added, T0);

        var exitCode = Run();

        Assert.Equal(0, exitCode);
        Assert.Contains("No deleted files recorded.", _console.Output);
    }

    [Fact]
    public void An_unparseable_since_value_is_reported_as_an_error()
    {
        _fixture.Record("live.txt", "x", FileChangeKind.Added, T0);

        var exitCode = Run("--since", "not-a-date");

        Assert.Equal(1, exitCode);
        Assert.Contains("--since", _errors.Output);
    }
}
