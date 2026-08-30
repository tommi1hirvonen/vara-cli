using Vara.Core.Configuration;

namespace Vara.Core.Abstractions;

/// <summary>
/// Loads and validates profile configuration from a YAML configuration file.
/// </summary>
public interface IProfileConfigLoader
{
    /// <summary>
    /// Loads and validates every profile defined in the configuration file at <paramref name="configPath"/>.
    /// </summary>
    /// <exception cref="ProfileConfigNotFoundException">The file does not exist.</exception>
    /// <exception cref="ProfileValidationException">A profile fails structural validation.</exception>
    /// <exception cref="DuplicateProfileNameException">Two profiles share the same name.</exception>
    IReadOnlyList<Profile> LoadProfiles(string configPath);

    /// <summary>
    /// The default configuration file path (<c>~/.vara/profiles.yml</c>).
    /// </summary>
    string DefaultConfigPath { get; }
}
