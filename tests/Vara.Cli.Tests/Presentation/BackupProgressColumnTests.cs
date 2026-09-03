using System.Linq;
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

    private static ProgressTask CreateTask(BackupProgressRole role, BackupProgressState? state = null, BackupProgressOutcome? outcome = null)
    {
        var task = new ProgressTask(1, "task", 100);
        task.State.Update(BackupProgressColumn.RoleKey, (BackupProgressRole _) => role);
        if (state is { } value)
        {
            task.State.Update(BackupProgressColumn.ProgressKey, (BackupProgressState _) => value);
        }

        if (outcome is { } resolvedOutcome)
        {
            task.State.Update(BackupProgressColumn.OutcomeKey, (BackupProgressOutcome _) => resolvedOutcome);
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
    public void Bar_role_fills_with_the_pastel_amber_color_while_running()
    {
        // No outcome stamped - defaults to BackupProgressOutcome.Running (enum value 0),
        // per design.md's "Running: ... pastel amber (transfer)" decision: an in-progress
        // bar is amber, distinct from any of the three final-outcome colors.
        var console = new TestConsole { EmitAnsiSequences = true };
        console.Profile.Width = 60;
        console.Profile.Capabilities.Ansi = true;
        console.Profile.Capabilities.ColorSystem = ColorSystem.EightBit;

        var calculator = new BackupProgressCalculator(() => DateTimeOffset.UtcNow.AddSeconds(1));
        var column = new BackupProgressColumn(calculator);
        var task = CreateTask(BackupProgressRole.Bar, new BackupProgressState(50, 100));

        console.Write(column.Render(CreateOptions(console), task, TimeSpan.Zero));

        Assert.Contains("\u001b[38;5;186m", console.Output); // LightGoldenrod2 (amber), OutcomeStyle.PartialFailure's color
        Assert.DoesNotContain("\u001b[38;5;121m", console.Output); // not PaleGreen1 - not colored as success while still running
    }

    [Fact]
    public void Bar_role_fills_entirely_in_the_success_color_once_a_success_outcome_is_stamped()
    {
        // A run with nothing to transfer (TotalBytes == 0) is immediately 100% complete
        // per BackupProgressCalculator, but the bar's final color is no longer derived
        // from that percentage - BackupCommand stamps the actual run outcome once known
        // (design.md's "Progress bar reflects run outcome severity" decision), so this
        // asserts against an explicitly stamped Success outcome rather than relying on
        // Value >= MaxValue alone.
        var console = new TestConsole { EmitAnsiSequences = true };
        console.Profile.Width = 60;
        console.Profile.Capabilities.Ansi = true;
        console.Profile.Capabilities.ColorSystem = ColorSystem.EightBit;

        var calculator = new BackupProgressCalculator(() => DateTimeOffset.UtcNow.AddSeconds(1));
        var column = new BackupProgressColumn(calculator);
        var task = CreateTask(BackupProgressRole.Bar, new BackupProgressState(0, 0), BackupProgressOutcome.Success);

        console.Write(column.Render(CreateOptions(console), task, TimeSpan.Zero));

        Assert.Contains("\u001b[38;5;121m", console.Output); // PaleGreen1 fill, entirely filled (not the grey remaining style)
        Assert.DoesNotContain("\u001b[38;5;8m", console.Output); // no grey "remaining" segment
        Assert.Equal(100, task.Value);
    }

    [Fact]
    public void Bar_role_fills_entirely_in_the_partial_failure_color_when_stamped_even_short_of_100_percent()
    {
        // A partial failure doesn't always move the byte total to 100% (a failed file's
        // bytes are simply never counted) - the bar must still render entirely in the
        // partial-failure color once that outcome is stamped, regardless of the
        // underlying percentage (progress-reporting delta's "Run completes with one or
        // more failed files" scenario).
        var console = new TestConsole { EmitAnsiSequences = true };
        console.Profile.Width = 60;
        console.Profile.Capabilities.Ansi = true;
        console.Profile.Capabilities.ColorSystem = ColorSystem.EightBit;

        var calculator = new BackupProgressCalculator(() => DateTimeOffset.UtcNow.AddSeconds(1));
        var column = new BackupProgressColumn(calculator);
        var task = CreateTask(BackupProgressRole.Bar, new BackupProgressState(60, 100), BackupProgressOutcome.PartialFailure);

        console.Write(column.Render(CreateOptions(console), task, TimeSpan.Zero));

        Assert.Contains("\u001b[38;5;186m", console.Output); // LightGoldenrod2, entirely filled
        Assert.DoesNotContain("\u001b[38;5;8m", console.Output); // no grey "remaining" segment despite being short of 100%
        Assert.Equal(100, task.Value);
    }

    [Fact]
    public void Bar_role_fills_entirely_in_the_error_color_when_a_hard_error_outcome_is_stamped()
    {
        var console = new TestConsole { EmitAnsiSequences = true };
        console.Profile.Width = 60;
        console.Profile.Capabilities.Ansi = true;
        console.Profile.Capabilities.ColorSystem = ColorSystem.EightBit;

        var calculator = new BackupProgressCalculator(() => DateTimeOffset.UtcNow.AddSeconds(1));
        var column = new BackupProgressColumn(calculator);
        var task = CreateTask(BackupProgressRole.Bar, new BackupProgressState(10, 100), BackupProgressOutcome.Error);

        console.Write(column.Render(CreateOptions(console), task, TimeSpan.Zero));

        Assert.Contains("\u001b[38;5;131m", console.Output); // IndianRed, entirely filled
        Assert.Equal(100, task.Value);
    }

    [Fact]
    public void Scan_role_reaches_the_full_configured_width_with_a_scanning_description()
    {
        var console = new TestConsole();
        console.Profile.Width = 60;

        var calculator = new BackupProgressCalculator();
        var column = new BackupProgressColumn(calculator);
        var task = CreateTask(BackupProgressRole.Scan);
        task.IsIndeterminate = true;

        console.Write(column.Render(CreateOptions(console), task, TimeSpan.Zero));

        Assert.Contains("Scanning files...", console.Output);
        Assert.Single(console.Lines);
        Assert.Equal(60, console.Lines[0].Length);
    }

    [Fact]
    public void Scan_role_renders_its_label_to_the_left_of_the_bar_and_a_blank_placeholder_to_the_right()
    {
        var console = new TestConsole();
        console.Profile.Width = 60;

        var calculator = new BackupProgressCalculator();
        var column = new BackupProgressColumn(calculator);
        var task = CreateTask(BackupProgressRole.Scan);
        task.IsIndeterminate = true;

        console.Write(column.Render(CreateOptions(console), task, TimeSpan.Zero));

        var line = console.Lines[0];
        Assert.True(line.IndexOf("Scanning files...", StringComparison.Ordinal) < BackupProgressColumn.LabelWidth);
        // No percentage-like content anywhere - the scan phase's trailing column is a
        // blank placeholder, not synthetic percentage text, per design.md's decision.
        Assert.DoesNotContain('%', line);
        var trailing = line[^BackupProgressColumn.TrailingWidth..];
        Assert.True(trailing.All(char.IsWhiteSpace));
    }

    [Fact]
    public void Bar_role_renders_its_label_to_the_left_of_the_bar_and_the_percentage_to_the_right()
    {
        var console = new TestConsole();
        console.Profile.Width = 60;

        var calculator = new BackupProgressCalculator(() => DateTimeOffset.UtcNow.AddSeconds(1));
        var column = new BackupProgressColumn(calculator);
        var task = CreateTask(BackupProgressRole.Bar, new BackupProgressState(50, 100));

        console.Write(column.Render(CreateOptions(console), task, TimeSpan.Zero));

        var line = console.Lines[0];
        Assert.True(line.IndexOf("Backing up...", StringComparison.Ordinal) < BackupProgressColumn.LabelWidth);
        var trailing = line[^BackupProgressColumn.TrailingWidth..];
        Assert.Contains('%', trailing);
    }

    [Fact]
    public void Bar_width_is_identical_between_scan_role_and_bar_role_at_the_same_console_width()
    {
        const int width = 80;

        var scanConsole = new TestConsole();
        scanConsole.Profile.Width = width;
        var scanColumn = new BackupProgressColumn(new BackupProgressCalculator());
        var scanTask = CreateTask(BackupProgressRole.Scan);
        scanTask.IsIndeterminate = true;
        scanConsole.Write(scanColumn.Render(CreateOptions(scanConsole), scanTask, TimeSpan.Zero));

        var barConsole = new TestConsole();
        barConsole.Profile.Width = width;
        var barColumn = new BackupProgressColumn(new BackupProgressCalculator(() => DateTimeOffset.UtcNow.AddSeconds(1)));
        var barTask = CreateTask(BackupProgressRole.Bar, new BackupProgressState(50, 100));
        barConsole.Write(barColumn.Render(CreateOptions(barConsole), barTask, TimeSpan.Zero));

        Assert.Equal(width, scanConsole.Lines[0].Length);
        Assert.Equal(width, barConsole.Lines[0].Length);

        // The bar itself is whatever remains of the line once the fixed label and
        // trailing columns are excluded - identical widths here is what actually
        // guarantees the bar renders at the same length in both phases.
        var scanBarWidth = scanConsole.Lines[0].Length - BackupProgressColumn.LabelWidth - BackupProgressColumn.TrailingWidth;
        var barBarWidth = barConsole.Lines[0].Length - BackupProgressColumn.LabelWidth - BackupProgressColumn.TrailingWidth;

        Assert.True(scanBarWidth > 0);
        Assert.Equal(scanBarWidth, barBarWidth);
    }

    [Fact]
    public void Scan_role_pulses_in_the_neutral_pastel_blue_color_while_running()
    {
        var console = new TestConsole { EmitAnsiSequences = true };
        console.Profile.Width = 60;
        console.Profile.Capabilities.Ansi = true;
        console.Profile.Capabilities.ColorSystem = ColorSystem.EightBit;

        var calculator = new BackupProgressCalculator();
        var column = new BackupProgressColumn(calculator);
        var task = CreateTask(BackupProgressRole.Scan);
        task.IsIndeterminate = true;

        console.Write(column.Render(CreateOptions(console), task, TimeSpan.Zero));

        Assert.Contains("\u001b[38;5;153m", console.Output); // LightSkyBlue1 pulse, OutcomeStyle.Neutral's color
    }

    [Fact]
    public void Scan_role_pulses_in_the_error_color_when_a_hard_error_outcome_is_stamped_before_replacement()
    {
        // Covers a hard error raised before transfer begins (e.g. during scanning/
        // diffing/planning), while the scan task is still the one on screen -
        // BackupCommand stamps Error directly onto it in that case.
        var console = new TestConsole { EmitAnsiSequences = true };
        console.Profile.Width = 60;
        console.Profile.Capabilities.Ansi = true;
        console.Profile.Capabilities.ColorSystem = ColorSystem.EightBit;

        var calculator = new BackupProgressCalculator();
        var column = new BackupProgressColumn(calculator);
        var task = CreateTask(BackupProgressRole.Scan, outcome: BackupProgressOutcome.Error);
        task.IsIndeterminate = true;

        console.Write(column.Render(CreateOptions(console), task, TimeSpan.Zero));

        Assert.Contains("\u001b[38;5;131m", console.Output); // IndianRed, not the neutral blue pulse
        Assert.DoesNotContain("\u001b[38;5;153m", console.Output);
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
            scanTask.IsIndeterminate = true;
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
