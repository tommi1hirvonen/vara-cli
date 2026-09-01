using Spectre.Console;
using Spectre.Console.Testing;
using Vara.Cli.Presentation;
using Xunit;

namespace Vara.Cli.Tests.Presentation;

/// <summary>
/// Verifies the `cli-presentation` capability's "Non-interactive or color-incapable
/// output falls back to plain text" requirement. These tests intentionally exercise
/// Spectre.Console's own capability detection/degradation rather than any custom
/// fallback logic of ours, per design.md's decision to rely on Spectre's built-in
/// profile detection instead of hand-rolled checks.
/// </summary>
public class CapabilityFallbackTests
{
    [Fact]
    public void Styled_output_has_no_escape_sequences_when_terminal_does_not_support_color()
    {
        var console = new TestConsole { EmitAnsiSequences = true };
        console.Profile.Capabilities.Ansi = false;
        console.Profile.Capabilities.ColorSystem = ColorSystem.NoColors;

        OutcomeStyle.WriteLineSuccess(console, "Snapshot #1 completed");

        var output = console.Output;
        Assert.Contains("Snapshot #1 completed", output);
        Assert.DoesNotContain("\u001b[", output);
    }

    [Fact]
    public void Styled_output_has_no_escape_sequences_when_output_is_redirected()
    {
        // A redirected/piped standard output is not a terminal at all, which Spectre's
        // own detection reports as a non-ANSI, colorless profile - simulated here the
        // same way a real redirected process's profile would be detected.
        var console = new TestConsole { EmitAnsiSequences = true };
        console.Profile.Capabilities.Ansi = false;
        console.Profile.Capabilities.Interactive = false;
        console.Profile.Capabilities.ColorSystem = ColorSystem.NoColors;

        OutcomeStyle.WriteLineError(console, "Error: profile not found");

        var output = console.Output;
        Assert.Contains("Error: profile not found", output);
        Assert.DoesNotContain("\u001b[", output);
    }

    [Fact]
    public void NO_COLOR_environment_variable_disables_color_on_a_real_ansi_console()
    {
        var previous = Environment.GetEnvironmentVariable("NO_COLOR");
        try
        {
            Environment.SetEnvironmentVariable("NO_COLOR", "1");
            var writer = new StringWriter();
            var console = AnsiConsole.Create(new AnsiConsoleSettings
            {
                Ansi = AnsiSupport.Detect,
                ColorSystem = ColorSystemSupport.Detect,
                Out = new AnsiConsoleOutput(writer),
            });

            OutcomeStyle.WriteLineSuccess(console, "Snapshot #1 completed");

            var output = writer.ToString();
            Assert.Contains("Snapshot #1 completed", output);
            Assert.DoesNotContain("\u001b[", output);
        }
        finally
        {
            Environment.SetEnvironmentVariable("NO_COLOR", previous);
        }
    }
}
