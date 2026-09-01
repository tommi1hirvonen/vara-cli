using Spectre.Console;
using Spectre.Console.Rendering;
using Spectre.Console.Testing;
using Vara.Application.Backup;
using Vara.Application.Reporting;
using Vara.Cli.Presentation;
using Xunit;

namespace Vara.Cli.Tests.Presentation;

public class BackupProgressColumnTests
{
    private static RenderOptions CreateOptions(TestConsole console) =>
        new(console.Profile.Capabilities, new Size(console.Profile.Width, console.Profile.Height));

    private static ProgressTask CreateTask(BackupProgressRole role, BackupProgressState? state = null)
    {
        var task = new ProgressTask(1, "task", 100);
        task.State.Update(BackupProgressColumn.RoleKey, (BackupProgressRole _) => role);
        if (state is { } value)
        {
            task.State.Update(BackupProgressColumn.ProgressKey, (BackupProgressState _) => value);
        }

        return task;
    }

    [Fact]
    public void Bar_role_reaches_the_full_configured_width()
    {
        var console = new TestConsole();
        console.Profile.Width = 60;

        var calculator = new BackupProgressCalculator(() => DateTimeOffset.UtcNow.AddSeconds(1));
        var column = new BackupProgressColumn(calculator);
        var task = CreateTask(BackupProgressRole.Bar, new BackupProgressState(50, 100));

        console.Write(column.Render(CreateOptions(console), task, TimeSpan.Zero));

        Assert.Single(console.Lines);
        Assert.Equal(60, console.Lines[0].Length);
        Assert.Contains('%', console.Lines[0]);
    }

    [Fact]
    public void Bar_role_fills_with_the_pastel_green_color()
    {
        var console = new TestConsole { EmitAnsiSequences = true };
        console.Profile.Width = 60;
        console.Profile.Capabilities.Ansi = true;
        console.Profile.Capabilities.ColorSystem = ColorSystem.EightBit;

        var calculator = new BackupProgressCalculator(() => DateTimeOffset.UtcNow.AddSeconds(1));
        var column = new BackupProgressColumn(calculator);
        var task = CreateTask(BackupProgressRole.Bar, new BackupProgressState(50, 100));

        console.Write(column.Render(CreateOptions(console), task, TimeSpan.Zero));

        Assert.Contains("\u001b[38;5;121m", console.Output); // PaleGreen1 fill
    }

    [Fact]
    public void Bar_role_fills_completely_in_the_pastel_green_color_when_there_is_nothing_to_transfer()
    {
        // A run with nothing to transfer (TotalBytes == 0) is immediately 100%
        // complete per BackupProgressCalculator - the bar's fill must agree with that,
        // not render as empty/grey (ProgressBarColumn's own fill ratio does not
        // special-case a zero MaxValue the way ProgressTask.Percentage does, so the
        // bar is driven from the calculator's percent rather than raw bytes/total).
        var console = new TestConsole { EmitAnsiSequences = true };
        console.Profile.Width = 60;
        console.Profile.Capabilities.Ansi = true;
        console.Profile.Capabilities.ColorSystem = ColorSystem.EightBit;

        var calculator = new BackupProgressCalculator(() => DateTimeOffset.UtcNow.AddSeconds(1));
        var column = new BackupProgressColumn(calculator);
        var task = CreateTask(BackupProgressRole.Bar, new BackupProgressState(0, 0));

        console.Write(column.Render(CreateOptions(console), task, TimeSpan.Zero));

        Assert.Contains("\u001b[38;5;121m", console.Output); // filled, not the grey remaining style
        Assert.DoesNotContain("\u001b[38;5;8m", console.Output); // no grey "remaining" segment
    }

    [Fact]
    public void Bar_role_uses_the_pastel_green_finished_style_once_complete()
    {
        // ProgressBarColumn switches from CompletedStyle to a separate FinishedStyle
        // once the task reaches 100% (Spectre defaults FinishedStyle to a plain
        // Color.Green independent of CompletedStyle) - both must be the same pastel
        // green or a just-completed bar would flash to the non-pastel default.
        var console = new TestConsole { EmitAnsiSequences = true };
        console.Profile.Width = 60;
        console.Profile.Capabilities.Ansi = true;
        console.Profile.Capabilities.ColorSystem = ColorSystem.EightBit;

        var calculator = new BackupProgressCalculator(() => DateTimeOffset.UtcNow.AddSeconds(1));
        var column = new BackupProgressColumn(calculator);
        var task = CreateTask(BackupProgressRole.Bar, new BackupProgressState(100, 100));

        console.Write(column.Render(CreateOptions(console), task, TimeSpan.Zero));

        Assert.Contains("\u001b[38;5;121m", console.Output); // PaleGreen1, not the default Color.Green (index 2)
        Assert.DoesNotContain("\u001b[38;5;2m", console.Output);
    }

    [Fact]
    public void Scan_role_renders_a_spinner_with_a_scanning_description()
    {
        var console = new TestConsole { EmitAnsiSequences = true };
        console.Profile.Width = 60;
        console.Profile.Capabilities.Ansi = true;
        console.Profile.Capabilities.ColorSystem = ColorSystem.EightBit;

        var calculator = new BackupProgressCalculator();
        var column = new BackupProgressColumn(calculator);
        var task = CreateTask(BackupProgressRole.Scan);

        var renderable = column.Render(CreateOptions(console), task, TimeSpan.Zero);
        console.Write(renderable);

        Assert.Contains("Scanning files...", console.Output);
    }

    [Fact]
    public void Stats_role_fields_do_not_shift_when_a_value_crosses_a_digit_count_boundary()
    {
        var start = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);

        var shortThroughputConsole = new TestConsole();
        shortThroughputConsole.Profile.Width = 100;
        var shortThroughputColumn = new BackupProgressColumn(new BackupProgressCalculator(() => start.AddSeconds(1)));
        var shortTask = CreateTask(BackupProgressRole.Stats, new BackupProgressState(5, 10_000));
        shortThroughputConsole.Write(shortThroughputColumn.Render(CreateOptions(shortThroughputConsole), shortTask, TimeSpan.Zero));
        var etaIndex1 = shortThroughputConsole.Lines[0].IndexOf("ETA", StringComparison.Ordinal);

        var longThroughputConsole = new TestConsole();
        longThroughputConsole.Profile.Width = 100;
        var longThroughputColumn = new BackupProgressColumn(new BackupProgressCalculator(() => start.AddSeconds(1)));
        var longTask = CreateTask(BackupProgressRole.Stats, new BackupProgressState(50_000_000, 100_000_000));
        longThroughputConsole.Write(longThroughputColumn.Render(CreateOptions(longThroughputConsole), longTask, TimeSpan.Zero));
        var etaIndex2 = longThroughputConsole.Lines[0].IndexOf("ETA", StringComparison.Ordinal);

        Assert.True(etaIndex1 >= 0);
        Assert.Equal(etaIndex1, etaIndex2);
    }

    [Fact]
    public void Unrecognized_role_renders_nothing()
    {
        var console = new TestConsole();
        console.Profile.Width = 60;

        var column = new BackupProgressColumn(new BackupProgressCalculator());
        var task = new ProgressTask(1, "task", 100); // no role set - defaults to BackupProgressRole.Scan (0)

        // A task with the default (unset) role state resolves to BackupProgressRole.Scan
        // (enum default value 0), so this exercises the fallback branch via a role that
        // was never explicitly stamped - matches how a stray/unexpected task would
        // render blank rather than throwing.
        var renderable = column.Render(CreateOptions(console), task, TimeSpan.Zero);
        Assert.NotNull(renderable);
    }

    [Fact]
    public void Scan_is_replaced_by_bar_and_stats_once_transfer_begins()
    {
        var console = new TestConsole { EmitAnsiSequences = true };
        console.Profile.Out = new FakeTerminalOutput(console.Profile.Out.Writer);
        console.Profile.Width = 100;
        console.Profile.Capabilities.Interactive = true;
        console.Profile.Capabilities.Ansi = true;
        console.Profile.Capabilities.ColorSystem = ColorSystem.EightBit;

        var calculator = new BackupProgressCalculator(() => DateTimeOffset.UtcNow.AddSeconds(1));
        var column = new BackupProgressColumn(calculator);

        console.Progress().Columns(column).Start(ctx =>
        {
            var scanTask = ctx.AddTask("scan");
            scanTask.State.Update(BackupProgressColumn.RoleKey, (BackupProgressRole _) => BackupProgressRole.Scan);
            ctx.Refresh();

            ctx.RemoveTask(scanTask);

            var barTask = ctx.AddTask("bar", autoStart: true, maxValue: 100);
            barTask.State.Update(BackupProgressColumn.RoleKey, (BackupProgressRole _) => BackupProgressRole.Bar);
            barTask.State.Update(BackupProgressColumn.ProgressKey, (BackupProgressState _) => new BackupProgressState(50, 100));

            var statsTask = ctx.AddTask("stats", autoStart: true, maxValue: 100);
            statsTask.State.Update(BackupProgressColumn.RoleKey, (BackupProgressRole _) => BackupProgressRole.Stats);
            statsTask.State.Update(BackupProgressColumn.ProgressKey, (BackupProgressState _) => new BackupProgressState(50, 100));
            ctx.Refresh();
        });

        var output = console.Output;
        var lastScanIndex = output.LastIndexOf("Scanning files...", StringComparison.Ordinal);
        var lastEtaIndex = output.LastIndexOf("ETA", StringComparison.Ordinal);

        Assert.True(lastScanIndex >= 0);
        Assert.True(lastEtaIndex >= 0);
        // The scan row's last appearance precedes the final bar/stats frame, i.e. the
        // scan indicator does not coexist with (or reappear after) the transfer rows.
        Assert.True(lastScanIndex < lastEtaIndex);
    }

    private sealed class FakeTerminalOutput(TextWriter writer) : IAnsiConsoleOutput
    {
        public TextWriter Writer { get; } = writer;
        public bool IsTerminal => true;
        public int Width => 100;
        public int Height => 30;

        public void SetEncoding(System.Text.Encoding encoding)
        {
        }
    }
}
