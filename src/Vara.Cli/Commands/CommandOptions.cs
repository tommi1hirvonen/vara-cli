using System.CommandLine;

namespace Vara.Cli.Commands;

/// <summary>Option definitions shared verbatim by several commands.</summary>
internal static class CommandOptions
{
    /// <summary>The <c>--config</c> option every profile-resolving command accepts.</summary>
    public static Option<string?> Config() => new("--config")
    {
        Description = "Path to the profiles configuration file (default: ~/.vara/profiles.yml)."
    };

    /// <summary>The <c>--profile</c> option of the commands that take the profile as an option.</summary>
    /// <param name="purpose">What the command does with the profile, for example "query" or "browse".</param>
    public static Option<string?> Profile(string purpose) => new("--profile")
    {
        Description = $"The profile to {purpose}. Optional when the current directory is inside a profile's target root."
    };
}
