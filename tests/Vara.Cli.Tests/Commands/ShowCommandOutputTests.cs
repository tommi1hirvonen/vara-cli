using Spectre.Console.Testing;
using Vara.Cli.Commands;
using Vara.Core.Snapshots;
using Xunit;

namespace Vara.Cli.Tests.Commands;

/// <summary>
/// Drives <see cref="ShowCommand"/> with an injected output stream and redirection flag, so the
/// streamed bytes and the interactive-terminal binary refusal can be asserted directly.
/// </summary>
public class ShowCommandOutputTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2025, 1, 10, 12, 0, 0, TimeSpan.Zero);

    private readonly ProfileFixture _fixture = new();
    private readonly MemoryStream _output = new();
    private readonly TestConsole _errors = new();

    public void Dispose() => _fixture.Dispose();

    private int Run(bool outputRedirected, params string[] args) =>
        ShowCommand.Create(_fixture.Resolver, _fixture.Services, _output, () => outputRedirected, _errors)
            .Parse([.. args, "--profile", "test-profile"]).Invoke();

    [Fact]
    public void Streams_the_selected_versions_exact_bytes()
    {
        var v1 = _fixture.Record("a.txt", "first", FileChangeKind.Added, T0);
        _fixture.Record("a.txt", "second", FileChangeKind.Changed, T0.AddDays(1));

        var exitCode = Run(outputRedirected: true, "a.txt", "--version", v1.ToString());

        Assert.Equal(0, exitCode);
        Assert.Equal("first", System.Text.Encoding.UTF8.GetString(_output.ToArray()));
    }

    [Fact]
    public void At_selects_the_version_current_as_of_that_time()
    {
        _fixture.Record("a.txt", "first", FileChangeKind.Added, T0);
        _fixture.Record("a.txt", "second", FileChangeKind.Changed, T0.AddDays(5));

        var exitCode = Run(outputRedirected: true, "a.txt", "--at", "2025-01-12");

        Assert.Equal(0, exitCode);
        Assert.Equal("first", System.Text.Encoding.UTF8.GetString(_output.ToArray()));
    }

    [Fact]
    public void Binary_content_is_refused_on_an_interactive_terminal()
    {
        var version = _fixture.Record("a.bin", "bin\0ary", FileChangeKind.Added, T0);

        var exitCode = Run(outputRedirected: false, "a.bin", "--version", version.ToString());

        Assert.Equal(1, exitCode);
        Assert.Empty(_output.ToArray());
        Assert.Contains("a.bin", _errors.Output);
    }

    [Fact]
    public void Force_binary_streams_binary_content_to_an_interactive_terminal()
    {
        var version = _fixture.Record("a.bin", "bin\0ary", FileChangeKind.Added, T0);

        var exitCode = Run(outputRedirected: false, "a.bin", "--version", version.ToString(), "--force-binary");

        Assert.Equal(0, exitCode);
        Assert.Equal("bin\0ary", System.Text.Encoding.UTF8.GetString(_output.ToArray()));
    }

    [Fact]
    public void Neither_at_nor_version_is_rejected()
    {
        var exitCode = Run(outputRedirected: true, "a.txt");

        Assert.Equal(1, exitCode);
        Assert.Contains("--at <date> or --version <id>", _errors.Output);
    }

    [Fact]
    public void Both_at_and_version_are_rejected()
    {
        var exitCode = Run(outputRedirected: true, "a.txt", "--at", "2025-01-12", "--version", "1");

        Assert.Equal(1, exitCode);
        Assert.Contains("mutually exclusive", _errors.Output);
    }

    [Fact]
    public void An_unknown_version_is_reported_as_an_error_and_writes_nothing()
    {
        _fixture.Record("a.txt", "first", FileChangeKind.Added, T0);

        var exitCode = Run(outputRedirected: true, "a.txt", "--version", "9999");

        Assert.Equal(1, exitCode);
        Assert.Empty(_output.ToArray());
        Assert.NotEqual(string.Empty, _errors.Output);
    }
}
