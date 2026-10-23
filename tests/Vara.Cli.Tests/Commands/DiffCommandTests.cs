using Spectre.Console.Testing;
using Vara.Cli.Commands;
using Vara.Core.Snapshots;
using Xunit;

namespace Vara.Cli.Tests.Commands;

public class DiffCommandTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2025, 1, 10, 12, 0, 0, TimeSpan.Zero);

    private readonly ProfileFixture _fixture = new();
    private readonly TestConsole _console = new();
    private readonly TestConsole _errors = new();

    public void Dispose() => _fixture.Dispose();

    private int Run(params string[] args) =>
        DiffCommand.Create(_fixture.Resolver, _fixture.Services, _console, _errors)
            .Parse([.. args, "--profile", "test-profile"]).Invoke();

    // Option-combination validation happens before any profile resolution, so the
    // validation tests need no recorded state.
    [Theory]
    [InlineData("--left-at", "2025-01-15", "--left-version", "5", "--right-at", "2025-01-16")]
    [InlineData("--left-at", "2025-01-15", "--right-at", "2025-01-16", "--right-version", "5")]
    public void Combining_the_at_and_version_form_of_one_side_is_rejected(params string[] options)
    {
        var exitCode = Run(["src", .. options]);

        Assert.Equal(1, exitCode);
        Assert.Contains("mutually exclusive", _errors.Output);
    }

    [Fact]
    public void Leaving_the_left_side_unspecified_is_rejected()
    {
        var exitCode = Run("src", "--right-at", "2025-01-16");

        Assert.Equal(1, exitCode);
        Assert.Contains("--left-at <date> or --left-version <id>", _errors.Output);
    }

    [Fact]
    public void Leaving_the_right_side_unspecified_is_rejected()
    {
        var exitCode = Run("src", "--left-version", "1");

        Assert.Equal(1, exitCode);
        Assert.Contains("--right-at <date> or --right-version <id>", _errors.Output);
    }

    [Fact]
    public void Renders_added_and_removed_lines_between_two_versions()
    {
        var v1 = _fixture.Record("a.txt", "keep\nold line\n", FileChangeKind.Added, T0);
        var v2 = _fixture.Record("a.txt", "keep\nnew line\n", FileChangeKind.Changed, T0.AddDays(1));

        var exitCode = Run("a.txt", "--left-version", v1.ToString(), "--right-version", v2.ToString());

        Assert.Equal(0, exitCode);
        Assert.Contains("  keep", _console.Output);
        Assert.Contains("- old line", _console.Output);
        Assert.Contains("+ new line", _console.Output);
    }

    [Fact]
    public void Versions_can_be_selected_by_date()
    {
        _fixture.Record("a.txt", "before\n", FileChangeKind.Added, T0);
        _fixture.Record("a.txt", "after\n", FileChangeKind.Changed, T0.AddDays(5));

        var exitCode = Run("a.txt", "--left-at", "2025-01-12", "--right-at", "2025-01-20");

        Assert.Equal(0, exitCode);
        Assert.Contains("- before", _console.Output);
        Assert.Contains("+ after", _console.Output);
    }

    [Fact]
    public void Binary_content_is_refused_with_a_message_and_a_hard_error_code()
    {
        var v1 = _fixture.Record("a.bin", "text", FileChangeKind.Added, T0);
        var v2 = _fixture.Record("a.bin", "bin\0ary", FileChangeKind.Changed, T0.AddDays(1));

        var exitCode = Run("a.bin", "--left-version", v1.ToString(), "--right-version", v2.ToString());

        Assert.Equal(1, exitCode);
        Assert.Contains("a.bin", _errors.Output);
        Assert.Equal(string.Empty, _console.Output);
    }

    [Fact]
    public void A_path_with_no_history_is_reported_as_an_error()
    {
        var exitCode = Run("missing.txt", "--left-version", "1", "--right-version", "2");

        Assert.Equal(1, exitCode);
        Assert.Contains("missing.txt", _errors.Output);
    }
}
