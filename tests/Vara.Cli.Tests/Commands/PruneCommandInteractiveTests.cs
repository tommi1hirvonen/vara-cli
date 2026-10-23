using Spectre.Console.Testing;
using Vara.Cli.Commands;
using Vara.Core.Configuration;
using Vara.Core.Snapshots;
using Xunit;

namespace Vara.Cli.Tests.Commands;

/// <summary>
/// Drives <see cref="PruneCommand"/>'s interactive paths - the confirmation prompt, the live
/// progress display and the cancellation outcome - through the command's injectable consoles
/// and input-redirection flag.
/// </summary>
public class PruneCommandInteractiveTests : IDisposable
{
    private static readonly DateTimeOffset Day1 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly ProfileFixture _fixture = new(new RetentionPolicy(keepDaily: 1, keepWeekly: 0, keepMonthly: 0, keepYearly: 0));
    private readonly TestConsole _prompts = new();

    public PruneCommandInteractiveTests()
    {
        _prompts.Profile.Capabilities.Interactive = true;
        _fixture.Record("a.txt", "one", FileChangeKind.Added, Day1);
        _fixture.Record("a.txt", "two", FileChangeKind.Changed, Day1.AddDays(1));
        _fixture.Record("a.txt", "three", FileChangeKind.Changed, Day1.AddDays(2));
    }

    public void Dispose() => _fixture.Dispose();

    private int Run(TestConsole console, bool inputRedirected, params string[] args) =>
        PruneCommand.Create(
                _fixture.Resolver,
                _fixture.Services,
                console: console,
                errorConsole: _prompts,
                isInputRedirected: () => inputRedirected)
            .Parse(["test-profile", .. args]).Invoke();

    [Fact]
    public void Confirming_the_prompt_removes_the_eligible_snapshots()
    {
        _prompts.Input.PushTextWithEnter("y");
        var console = new TestConsole();

        var exitCode = Run(console, inputRedirected: false);

        Assert.Equal(0, exitCode);
        Assert.Contains("remove 2 snapshot(s)", _prompts.Output);
        Assert.Single(_fixture.SnapshotIds());
    }

    [Fact]
    public void Declining_the_prompt_removes_nothing_and_reports_the_cancellation()
    {
        _prompts.Input.PushTextWithEnter("n");
        var console = new TestConsole();

        var exitCode = Run(console, inputRedirected: false);

        Assert.Equal(0, exitCode);
        Assert.Contains("Prune cancelled: no snapshots removed.", console.Output);
        Assert.Equal(3, _fixture.SnapshotIds().Count);
    }

    [Fact]
    public void A_redirected_input_without_yes_is_refused_with_a_hint_and_removes_nothing()
    {
        var exitCode = Run(new TestConsole(), inputRedirected: true);

        Assert.Equal(1, exitCode);
        Assert.Contains("--yes", _prompts.Output);
        Assert.Equal(3, _fixture.SnapshotIds().Count);
    }

    [Fact]
    public void Yes_on_a_live_terminal_prunes_through_the_progress_display()
    {
        var console = TestConsoles.CreateLive();

        var exitCode = Run(console, inputRedirected: true, "--yes");

        Assert.Equal(0, exitCode);
        Assert.Single(_fixture.SnapshotIds());
        Assert.Equal(string.Empty, _prompts.Output);
    }

    [Fact]
    public void Yes_on_a_plain_console_prunes_and_prints_the_evaluation_line()
    {
        var console = new TestConsole();

        var exitCode = Run(console, inputRedirected: true, "--yes");

        Assert.Equal(0, exitCode);
        Assert.Contains("Evaluating...", console.Output);
        Assert.Single(_fixture.SnapshotIds());
    }
}
