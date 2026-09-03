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
    private const string ManifestRelativePath = @".vara\profile.db";

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

    /// <summary>
    /// Resolves a profile for a browsing/restoring command, per the profile-config
    /// capability's "Profile selection by name" requirement: if <paramref name="profileName"/>
    /// is given, resolves it by name via <see cref="Resolve"/> exactly as before (the working
    /// directory is never consulted when a name is given). Otherwise, walks upward from
    /// <paramref name="startDirectory"/> (or the current working directory) looking for a
    /// <c>.vara\profile.db</c> file, the same way version control tools locate their own
    /// metadata directory, and constructs a synthetic <see cref="Profile"/> for the directory
    /// it is found in - without reading <c>~/.vara/profiles.yml</c> at all in that case.
    /// </summary>
    /// <exception cref="ProfileConfigNotFoundException">The configuration file does not exist (only when <paramref name="profileName"/> is given).</exception>
    /// <exception cref="UnknownProfileException">No profile with that name is defined (only when <paramref name="profileName"/> is given).</exception>
    /// <exception cref="ProfileNameRequiredException">No profile name was given, and no <c>.vara\profile.db</c> was found walking upward to the filesystem root.</exception>
    public Profile ResolveForBrowsing(string? profileName, string? configPath = null, string? startDirectory = null)
    {
        if (!string.IsNullOrWhiteSpace(profileName))
        {
            return Resolve(profileName, configPath);
        }

        return TryResolveFromWorkingDirectory(startDirectory, out var profile)
            ? profile
            : throw new ProfileNameRequiredException();
    }

    /// <summary>
    /// Walks upward from <paramref name="startDirectory"/> (or the current working directory)
    /// looking for a <c>.vara\profile.db</c> file. On success, <paramref name="profile"/> is a
    /// synthetic <see cref="Profile"/> whose target root is the directory the file was found
    /// in, whose name is that directory's folder name, and whose single placeholder
    /// <see cref="Source"/> is that same directory (never used for browsing/restoring, only
    /// present because <see cref="Profile"/>'s constructor requires at least one source).
    /// </summary>
    public static bool TryResolveFromWorkingDirectory(string? startDirectory, out Profile profile)
    {
        var directory = Path.GetFullPath(startDirectory ?? Directory.GetCurrentDirectory());

        while (true)
        {
            if (File.Exists(Path.Combine(directory, ManifestRelativePath)))
            {
                var name = Path.GetFileName(directory);
                if (string.IsNullOrEmpty(name))
                {
                    // directory is a drive root (e.g. "C:\"); Path.GetFileName returns "" there.
                    name = directory;
                }

                profile = new Profile(name, directory, [new Source(directory)], retention: null);
                return true;
            }

            var parent = Directory.GetParent(directory);
            if (parent is null)
            {
                profile = null!;
                return false;
            }

            directory = parent.FullName;
        }
    }
}

