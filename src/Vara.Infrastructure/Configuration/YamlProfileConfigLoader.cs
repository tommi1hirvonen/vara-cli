using Vara.Core.Abstractions;
using Vara.Core.Configuration;
using YamlDotNet.RepresentationModel;

namespace Vara.Infrastructure.Configuration;

/// <summary>
/// Loads and validates profile configuration from a YAML file (default:
/// <c>~/.vara/profiles.yml</c>). Uses YamlDotNet's low-level representation-model
/// (DOM) API rather than its reflection-based deserializer, so parsing is
/// Native AOT / trimming safe: the profile schema is small and known ahead of time,
/// so hand-mapping the parsed node tree needs no reflection.
/// </summary>
public sealed class YamlProfileConfigLoader : IProfileConfigLoader
{
    public string DefaultConfigPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".vara", "profiles.yml");

    public IReadOnlyList<Profile> LoadProfiles(string configPath)
    {
        if (!File.Exists(configPath))
        {
            throw new ProfileConfigNotFoundException(configPath);
        }

        using var reader = new StreamReader(configPath);
        var yamlStream = new YamlStream();
        yamlStream.Load(reader);

        if (yamlStream.Documents.Count == 0)
        {
            return [];
        }

        if (yamlStream.Documents[0].RootNode is not YamlMappingNode root)
        {
            return [];
        }

        if (!TryGetChild(root, "profiles", out var profilesValue) || profilesValue is not YamlSequenceNode profilesNode)
        {
            return [];
        }

        var profiles = new List<Profile>();
        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in profilesNode.Children)
        {
            var mapping = AsMapping(entry, "<unnamed>", "each entry under 'profiles' must be a mapping");
            var profile = ParseProfile(mapping);

            if (!seenNames.Add(profile.Name))
            {
                throw new DuplicateProfileNameException(profile.Name);
            }

            profiles.Add(profile);
        }

        return profiles;
    }

    private static Profile ParseProfile(YamlMappingNode mapping)
    {
        var name = GetOptionalScalar(mapping, "name");
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ProfileValidationException(name ?? "<unnamed>", "missing required field 'name'");
        }

        var target = GetOptionalScalar(mapping, "target");
        if (string.IsNullOrWhiteSpace(target))
        {
            throw new ProfileValidationException(name, "missing required field 'target'");
        }

        if (!TryGetChild(mapping, "sources", out var sourcesValue) || sourcesValue is not YamlSequenceNode sourcesNode || sourcesNode.Children.Count == 0)
        {
            throw new ProfileValidationException(name, "must define at least one source under 'sources'");
        }

        var sources = sourcesNode.Children
            .Select(entry => ParseSource(name, AsMapping(entry, name, "each entry under 'sources' must be a mapping")))
            .ToList();

        var retention = ParseRetention(mapping);

        return new Profile(name, target, sources, retention);
    }

    private static Source ParseSource(string profileName, YamlMappingNode mapping)
    {
        var path = GetOptionalScalar(mapping, "path");
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ProfileValidationException(profileName, "each source must define a 'path'");
        }

        var recursive = true;
        var recursiveText = GetOptionalScalar(mapping, "recursive");
        if (recursiveText is not null && !bool.TryParse(recursiveText, out recursive))
        {
            throw new ProfileValidationException(profileName, $"source '{path}' has an invalid 'recursive' value '{recursiveText}' (expected true/false)");
        }

        var excludes = GetOptionalStringList(mapping, "exclude");
        var includeGlobs = GetOptionalStringList(mapping, "include_globs");
        var excludeGlobs = GetOptionalStringList(mapping, "exclude_globs");

        return new Source(path, recursive, excludes, includeGlobs, excludeGlobs);
    }

    private static RetentionPolicy? ParseRetention(YamlMappingNode mapping)
    {
        if (!TryGetChild(mapping, "retention", out var retentionValue))
        {
            return null;
        }

        var retentionMapping = AsMapping(retentionValue, GetOptionalScalar(mapping, "name") ?? "<unnamed>", "'retention' must be a mapping");

        var keepDaily = GetOptionalInt(retentionMapping, "keep_daily") ?? 0;
        var keepWeekly = GetOptionalInt(retentionMapping, "keep_weekly") ?? 0;
        var keepMonthly = GetOptionalInt(retentionMapping, "keep_monthly") ?? 0;
        var keepYearly = GetOptionalInt(retentionMapping, "keep_yearly") ?? 0;

        return new RetentionPolicy(keepDaily, keepWeekly, keepMonthly, keepYearly);
    }

    private static YamlMappingNode AsMapping(YamlNode node, string profileName, string reason) =>
        node as YamlMappingNode ?? throw new ProfileValidationException(profileName, reason);

    private static bool TryGetChild(YamlMappingNode mapping, string key, out YamlNode value)
    {
        foreach (var (childKey, childValue) in mapping.Children)
        {
            if (childKey is YamlScalarNode scalarKey && string.Equals(scalarKey.Value, key, StringComparison.OrdinalIgnoreCase))
            {
                value = childValue;
                return true;
            }
        }

        value = null!;
        return false;
    }

    private static string? GetOptionalScalar(YamlMappingNode mapping, string key) =>
        TryGetChild(mapping, key, out var value) && value is YamlScalarNode scalar ? scalar.Value : null;

    private static int? GetOptionalInt(YamlMappingNode mapping, string key)
    {
        var text = GetOptionalScalar(mapping, key);
        return text is null ? null : int.Parse(text);
    }

    private static IReadOnlyList<string>? GetOptionalStringList(YamlMappingNode mapping, string key)
    {
        if (!TryGetChild(mapping, key, out var value) || value is not YamlSequenceNode sequence)
        {
            return null;
        }

        return sequence.Children.OfType<YamlScalarNode>().Select(n => n.Value ?? string.Empty).ToList();
    }
}
