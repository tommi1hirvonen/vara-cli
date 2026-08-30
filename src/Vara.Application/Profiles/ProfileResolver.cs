using Vara.Core.Abstractions;
using Vara.Core.Configuration;

namespace Vara.Application.Profiles;

/// <summary>
/// Loads the profile configuration and resolves a single profile by name, shared by
/// every command that operates on a single profile (backup, snapshots, history,
/// restore, prune).
/// </summary>
public sealed class ProfileResolver(IProfileConfigLoader configLoader)
{
    /// <summary>
    /// Loads profiles from <paramref name="configPath"/> (or the loader's default path)
    /// and returns the profile named <paramref name="profileName"/>.
    /// </summary>
    /// <exception cref="ProfileConfigNotFoundException">The configuration file does not exist.</exception>
    /// <exception cref="UnknownProfileException">No profile with that name is defined.</exception>
    public Profile Resolve(string profileName, string? configPath = null)
    {
        var profiles = configLoader.LoadProfiles(configPath ?? configLoader.DefaultConfigPath);

        foreach (var profile in profiles)
        {
            if (string.Equals(profile.Name, profileName, StringComparison.OrdinalIgnoreCase))
            {
                return profile;
            }
        }

        throw new UnknownProfileException(profileName);
    }
}
