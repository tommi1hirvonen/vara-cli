using Spectre.Console.Testing;
using Vara.Cli.Commands;
using Vara.Core.Snapshots;
using Xunit;

namespace Vara.Cli.Tests.Commands;

public class HistoryCommandTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2025, 1, 10, 12, 0, 0, TimeSpan.Zero);

    private readonly ProfileFixture _fixture = new();
    private readonly TestConsole _console = new();
    private readonly TestConsole _errors = new();

    public void Dispose() => _fixture.Dispose();

    private int Run(params string[] args) =>
        HistoryCommand.Create(_fixture.Resolver, _fixture.Services, _console, _errors)
            .Parse([.. args, "--profile", "test-profile"]).Invoke();

    [Fact]
    public void Lists_every_recorded_version_of_the_path_most_recent_first()
    {
        _fixture.Record("a.txt", "one", FileChangeKind.Added, T0);
        _fixture.Record("a.txt", "two!", FileChangeKind.Changed, T0.AddDays(1));

        var exitCode = Run("a.txt");

        Assert.Equal(0, exitCode);
        Assert.Contains("Added", _console.Output);
        Assert.Contains("Changed", _console.Output);
        Assert.True(
            _console.Output.IndexOf("Changed", StringComparison.Ordinal) <
            _console.Output.IndexOf("Added", StringComparison.Ordinal));
        Assert.Equal(string.Empty, _errors.Output);
    }

    [Fact]
    public void A_path_with_no_recorded_history_is_reported_as_an_error()
    {
        _fixture.Record("a.txt", "one", FileChangeKind.Added, T0);

        var exitCode = Run("missing.txt");

        Assert.Equal(1, exitCode);
        Assert.Contains("missing.txt", _errors.Output);
        Assert.DoesNotContain("Added", _console.Output);
    }

    [Fact]
    public void An_absolute_source_path_resolves_to_its_mirror_relative_history()
    {
        var mirrorPath = Path.Combine(
            Vara.Core.FileSystem.AbsolutePathMirrorMapper.ToMirrorPath(_fixture.SourceRoot), "a.txt");
        _fixture.Record(mirrorPath, "one", FileChangeKind.Added, T0);

        var exitCode = Run(Path.Combine(_fixture.SourceRoot, "a.txt"));

        Assert.Equal(0, exitCode);
        Assert.Contains("Added", _console.Output);
    }
}
