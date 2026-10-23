using Spectre.Console;
using Spectre.Console.Testing;
using Vara.Core.Abstractions;
using Vara.Core.Configuration;
using Profile = Vara.Core.Configuration.Profile;

namespace Vara.Cli.Tests;

/// <summary>An <see cref="IProfileConfigLoader"/> that always yields the one given profile.</summary>
internal sealed class SingleProfileConfigLoader(Profile profile) : IProfileConfigLoader
{
    public IReadOnlyList<Profile> LoadProfiles(string configPath) => [profile];
    public string DefaultConfigPath => "unused";
}

/// <summary>A loader for tests whose code path must never reach profile resolution.</summary>
internal sealed class UnusedProfileConfigLoader : IProfileConfigLoader
{
    public IReadOnlyList<Profile> LoadProfiles(string configPath) => throw new NotSupportedException();
    public string DefaultConfigPath => throw new NotSupportedException();
}

/// <summary>A hasher for tests whose code path must never hash content.</summary>
internal sealed class UnusedHasher : IHasher
{
    public string ComputeHash(Stream content) => throw new NotSupportedException();
}

/// <summary>An output that reports itself as a real terminal, so live displays are attempted.</summary>
internal sealed class FakeTerminalOutput(TextWriter writer, int width = 120) : IAnsiConsoleOutput
{
    public TextWriter Writer { get; } = writer;
    public bool IsTerminal => true;
    public int Width { get; } = width;
    public int Height => 30;

    public void SetEncoding(System.Text.Encoding encoding)
    {
    }
}

internal static class TestConsoles
{
    /// <summary>A <see cref="TestConsole"/> that <c>OutputMode.IsLiveCapable</c> treats as a terminal.</summary>
    public static TestConsole CreateLive(int width = 120)
    {
        var console = new TestConsole { EmitAnsiSequences = true };
        console.Profile.Out = new FakeTerminalOutput(console.Profile.Out.Writer, width);
        console.Profile.Capabilities.Ansi = true;
        console.Profile.Capabilities.Interactive = true;
        console.Profile.Width = width;
        return console;
    }
}
