using Spectre.Console;
using Vara.Cli.Presentation;
using Xunit;

namespace Vara.Cli.Tests.Presentation;

public class LiveProgressTests
{
    [Fact]
    public void Returns_the_bodys_result_and_runs_it_against_a_live_context()
    {
        var console = TestConsoles.CreateLive();
        ProgressContext? seen = null;

        var result = LiveProgress.Run(
            console,
            [new TaskDescriptionColumn()],
            (ctx, _) =>
            {
                seen = ctx;
                ctx.AddTask("work");
                return 42;
            });

        Assert.Equal(42, result);
        Assert.NotNull(seen);
    }

    [Fact]
    public void Propagates_an_exception_thrown_by_the_body()
    {
        var console = TestConsoles.CreateLive();

        var ex = Assert.Throws<InvalidOperationException>(() => LiveProgress.Run<int>(
            console,
            [new TaskDescriptionColumn()],
            (_, _) => throw new InvalidOperationException("boom")));

        Assert.Equal("boom", ex.Message);
    }

    [Fact]
    public void Passes_no_terminal_progress_for_a_console_that_is_not_the_ambient_terminal()
    {
        var console = TestConsoles.CreateLive();
        var sawTerminalProgress = false;

        LiveProgress.Run(
            console,
            [new TaskDescriptionColumn()],
            (_, progress) =>
            {
                sawTerminalProgress = progress is not null;
                return 0;
            },
            startIndeterminate: true);

        Assert.False(sawTerminalProgress);
    }
}
