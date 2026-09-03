namespace Vara.Core.Configuration;

/// <summary>
/// Base type for all profile-configuration-related errors that should be surfaced
/// to the user as clear, actionable messages rather than raw exceptions.
/// </summary>
public abstract class ProfileConfigException(string message) : Exception(message);

/// <summary>
/// The profile configuration file could not be found at its expected location.
/// </summary>
public sealed class ProfileConfigNotFoundException(string configPath)
    : ProfileConfigException($"Profile configuration file not found at '{configPath}'.")
{
    public string ConfigPath { get; } = configPath;
}

/// <summary>
/// The profile configuration file is structurally malformed (its root is not a
/// mapping, or a known top-level key has the wrong shape) rather than merely
/// defining zero profiles.
/// </summary>
public sealed class ProfileConfigMalformedException(string configPath, string reason)
    : ProfileConfigException($"Profile configuration file '{configPath}' is malformed: {reason}")
{
    public string ConfigPath { get; } = configPath;
    public string Reason { get; } = reason;
}

/// <summary>
/// A profile in the configuration file failed structural validation
/// (missing required field, invalid value, etc.).
/// </summary>
public sealed class ProfileValidationException(string profileName, string reason)
    : ProfileConfigException($"Profile '{profileName}' is invalid: {reason}")
{
    public string ProfileName { get; } = profileName;
    public string Reason { get; } = reason;
}

/// <summary>
/// The configuration file defines two or more profiles with the same name.
/// </summary>
public sealed class DuplicateProfileNameException(string profileName)
    : ProfileConfigException($"Profile name '{profileName}' is defined more than once in the configuration file.")
{
    public string ProfileName { get; } = profileName;
}

/// <summary>
/// A command referenced a profile name that does not exist in the configuration file.
/// </summary>
public sealed class UnknownProfileException(string profileName)
    : ProfileConfigException($"No profile named '{profileName}' was found in the configuration file.")
{
    public string ProfileName { get; } = profileName;
}

/// <summary>
/// A retention-dependent command was invoked for a profile without a configured retention policy.
/// </summary>
public sealed class RetentionPolicyNotConfiguredException(string profileName)
    : ProfileConfigException($"Profile '{profileName}' has no retention policy configured.")
{
    public string ProfileName { get; } = profileName;
}

/// <summary>
/// A browsing/restoring command was invoked with no profile name, and no <c>.vara\profile.db</c>
/// was found by walking upward from the current working directory to the filesystem root -
/// per the profile-config capability's "Profile selection by name" requirement, a profile name
/// is required whenever the working directory does not resolve one on its own.
/// </summary>
public sealed class ProfileNameRequiredException()
    : ProfileConfigException("No profile name was given, and the current directory is not inside a profile's target root. Specify a profile name.");

