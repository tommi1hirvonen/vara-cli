using Vara.Core.Abstractions;
using Vara.Core.Configuration;
using YamlDotNet.Core;
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

        try
        {
            yamlStream.Load(reader);
        }
        catch (YamlException ex)
        {
            throw new ProfileConfigMalformedException(configPath, $"could not be parsed as YAML - {ex.Message}");
        }

        if (yamlStream.Documents.Count == 0)
        {
            return [];
        }

        if (yamlStream.Documents[0].RootNode is not YamlMappingNode root)
        {
            throw new ProfileConfigMalformedException(configPath, "the document root must be a mapping");
        }

        if (!TryGetChild(root, "profiles", out var profilesValue))
        {
            return [];
        }

        if (profilesValue is not YamlSequenceNode profilesNode)
        {
            throw new ProfileConfigMalformedException(configPath, "'profiles' must be a list");
        }

        var profiles = new List<Profile>();

        foreach (var entry in profilesNode.Children)
        {
            var mapping = AsMapping(entry, "<unnamed>", "each entry under 'profiles' must be a mapping");
            var profile = ParseProfile(mapping);

            // Shares the same comparison logic the interactive profile editor's live
            // validation uses (see ProfileNameUniqueness), so "what counts as a duplicate
            // name" is defined once. excludedName is always null here - every name parsed so
            // far genuinely is a different, already-accepted profile from the one being
            // checked, unlike the editor's own-profile-rename case.
            if (ProfileNameUniqueness.ConflictsWithAnotherProfile(profiles.Select(p => p.Name), profile.Name, excludedName: null))
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
        var concurrency = ParseConcurrency(mapping);

        try
        {
            return new Profile(name, target, sources, retention, concurrency);
        }
        catch (ArgumentException ex) when (ex.ParamName is "targetRoot" or "sources")
        {
            throw new ProfileValidationException(name, ex.Message);
        }
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

        var excludes = GetOptionalStringList(mapping, profileName, path, "exclude");
        var includeGlobs = GetOptionalStringList(mapping, profileName, path, "include_globs");
        var excludeGlobs = GetOptionalStringList(mapping, profileName, path, "exclude_globs");

        try
        {
            return new Source(path, recursive, excludes, includeGlobs, excludeGlobs);
        }
        catch (ArgumentException ex) when (ex.ParamName == "path")
        {
            throw new ProfileValidationException(profileName, ex.Message);
        }
    }

    private static RetentionPolicy? ParseRetention(YamlMappingNode mapping)
    {
        if (!TryGetChild(mapping, "retention", out var retentionValue))
        {
            return null;
        }

        var profileName = GetOptionalScalar(mapping, "name") ?? "<unnamed>";
        var retentionMapping = AsMapping(retentionValue, profileName, "'retention' must be a mapping");

        var keepDaily = GetOptionalRetentionCount(retentionMapping, profileName, "keep_daily") ?? 0;
        var keepWeekly = GetOptionalRetentionCount(retentionMapping, profileName, "keep_weekly") ?? 0;
        var keepMonthly = GetOptionalRetentionCount(retentionMapping, profileName, "keep_monthly") ?? 0;
        var keepYearly = GetOptionalRetentionCount(retentionMapping, profileName, "keep_yearly") ?? 0;

        return new RetentionPolicy(keepDaily, keepWeekly, keepMonthly, keepYearly);
    }

    private static ConcurrencySettings? ParseConcurrency(YamlMappingNode mapping)
    {
        if (!TryGetChild(mapping, "concurrency", out var concurrencyValue))
        {
            return null;
        }

        var profileName = GetOptionalScalar(mapping, "name") ?? "<unnamed>";
        var concurrencyMapping = AsMapping(concurrencyValue, profileName, "'concurrency' must be a mapping");

        var scanConcurrency = GetOptionalPositiveConcurrency(concurrencyMapping, profileName, "scan_concurrency");
        var transferConcurrency = GetOptionalPositiveConcurrency(concurrencyMapping, profileName, "transfer_concurrency");

        return new ConcurrencySettings(scanConcurrency, transferConcurrency);
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

    private static int? GetOptionalRetentionCount(YamlMappingNode mapping, string profileName, string key)
    {
        var text = GetOptionalScalar(mapping, key);
        if (text is null)
        {
            return null;
        }

        if (!int.TryParse(text, out var value))
        {
            throw new ProfileValidationException(profileName, $"retention field '{key}' has an invalid value '{text}' (expected a non-negative whole number)");
        }

        if (value < 0)
        {
            throw new ProfileValidationException(profileName, $"retention field '{key}' has an invalid value '{text}' (expected a non-negative whole number)");
        }

        return value;
    }

    private static int? GetOptionalPositiveConcurrency(YamlMappingNode mapping, string profileName, string key)
    {
        var text = GetOptionalScalar(mapping, key);
        if (text is null)
        {
            return null;
        }

        if (!int.TryParse(text, out var value) || value <= 0)
        {
            throw new ProfileValidationException(profileName, $"concurrency field '{key}' has an invalid value '{text}' (expected a positive whole number)");
        }

        return value;
    }

    private static IReadOnlyList<string>? GetOptionalStringList(YamlMappingNode mapping, string profileName, string sourcePath, string key)
    {
        if (!TryGetChild(mapping, key, out var value))
        {
            return null;
        }

        if (value is not YamlSequenceNode sequence)
        {
            throw new ProfileValidationException(profileName, $"source '{sourcePath}' field '{key}' must be a list, but found {DescribeNodeKind(value)}");
        }

        var result = new List<string>(sequence.Children.Count);
        foreach (var entry in sequence.Children)
        {
            if (entry is not YamlScalarNode scalar)
            {
                throw new ProfileValidationException(profileName, $"source '{sourcePath}' field '{key}' contains an entry that is {DescribeNodeKind(entry)} instead of a string");
            }

            result.Add(scalar.Value ?? string.Empty);
        }

        return result;
    }

    private static string DescribeNodeKind(YamlNode node) => node switch
    {
        YamlMappingNode => "a mapping",
        YamlSequenceNode => "a nested list",
        YamlScalarNode => "a scalar",
        _ => "an unsupported node type",
    };
}
