using Vara.Core.Configuration;

namespace Vara.Core.Abstractions;

/// <summary>
/// Writes profile configuration to a YAML configuration file, mirroring
/// <see cref="IProfileConfigLoader"/>'s read path.
/// </summary>
public interface IProfileConfigWriter
{
    /// <summary>
    /// Regenerates the entire configuration file at <paramref name="configPath"/> from
    /// <paramref name="profiles"/>, replacing any previous content. The write is atomic
    /// (a temporary file is written first, then moved into place), so an interruption
    /// mid-write never leaves the file partially written.
    /// </summary>
    void WriteProfiles(IReadOnlyList<Profile> profiles, string configPath);
}
