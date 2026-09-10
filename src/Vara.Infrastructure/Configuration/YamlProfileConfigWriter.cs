using System.Globalization;
using Vara.Core.Abstractions;
using Vara.Core.Configuration;
using YamlDotNet.RepresentationModel;

namespace Vara.Infrastructure.Configuration;

/// <summary>
/// Regenerates <c>~/.vara/profiles.yml</c> (or an alternate configured path) from an
/// in-memory profile list. Builds YamlDotNet's low-level representation-model (DOM) node
/// types directly and serializes them via <see cref="YamlStream.Save(TextWriter, bool)"/>,
/// mirroring <see cref="YamlProfileConfigLoader"/>'s deliberate avoidance of YamlDotNet's
/// reflection-based serializer (<c>Vara.Cli</c> builds with <c>PublishAot=true</c>).
/// </summary>
public sealed class YamlProfileConfigWriter : IProfileConfigWriter
{
    public void WriteProfiles(IReadOnlyList<Profile> profiles, string configPath)
    {
        var document = BuildDocument(profiles);
        var yamlStream = new YamlStream(document);

        try
        {
            var directory = Path.GetDirectoryName(configPath);
            if (string.IsNullOrEmpty(directory))
            {
                directory = Directory.GetCurrentDirectory();
            }
            else
            {
                // First-write-safe: on a fresh machine, ~/.vara/ itself may not exist yet.
                Directory.CreateDirectory(directory);
            }

            // Written to a temp file in the same directory (same volume, so the final swap
            // below is atomic), then swapped into place - never partially written in place.
            var tempPath = Path.Combine(directory, $".{Path.GetFileName(configPath)}.{Guid.NewGuid():N}.tmp");

            try
            {
                using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
                using (var writer = new StreamWriter(stream))
                {
                    yamlStream.Save(writer, assignAnchors: false);
                    writer.Flush();
                    // Forces the OS to commit the temp file's bytes to durable storage
                    // before the swap below, rather than relying on StreamWriter.Dispose's
                    // flush - which only empties .NET's in-memory buffer into the OS's own
                    // write-behind cache, not onto disk - so the atomic-move guarantee
                    // (readable as either the whole old or whole new file) survives an
                    // abrupt loss of the process or machine, not only an orderly one.
                    stream.Flush(flushToDisk: true);
                }

                if (File.Exists(configPath))
                {
                    // File.Replace throws FileNotFoundException when the destination doesn't
                    // already exist, so it cannot be used unconditionally - see File.Move below
                    // for the first-ever-write case.
                    File.Replace(tempPath, configPath, destinationBackupFileName: null);
                }
                else
                {
                    File.Move(tempPath, configPath);
                }
            }
            finally
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Surfaced as one recognizable ProfileConfigException instead of a raw I/O
            // exception type, so a caller (ProfilesCommand's Save/delete actions) has a
            // single, catchable failure mode instead of enumerating file-system exception
            // types - see the harden-profiles-save-resilience change's design.md.
            throw new ProfileConfigWriteFailedException(configPath, ex.Message, ex);
        }
    }

    private static YamlDocument BuildDocument(IReadOnlyList<Profile> profiles)
    {
        var profilesSequence = new YamlSequenceNode();
        foreach (var profile in profiles)
        {
            profilesSequence.Add(BuildProfileMapping(profile));
        }

        var root = new YamlMappingNode();
        root.Add("profiles", profilesSequence);

        return new YamlDocument(root);
    }

    private static YamlMappingNode BuildProfileMapping(Profile profile)
    {
        var mapping = new YamlMappingNode();
        mapping.Add("name", profile.Name);
        mapping.Add("target", profile.TargetRoot);

        var sourcesSequence = new YamlSequenceNode();
        foreach (var source in profile.Sources)
        {
            sourcesSequence.Add(BuildSourceMapping(source));
        }

        mapping.Add("sources", sourcesSequence);

        if (profile.Retention is { } retention)
        {
            var retentionMapping = new YamlMappingNode();
            retentionMapping.Add("keep_daily", ToInvariantString(retention.KeepDaily));
            retentionMapping.Add("keep_weekly", ToInvariantString(retention.KeepWeekly));
            retentionMapping.Add("keep_monthly", ToInvariantString(retention.KeepMonthly));
            retentionMapping.Add("keep_yearly", ToInvariantString(retention.KeepYearly));
            mapping.Add("retention", retentionMapping);
        }

        if (profile.Concurrency is { } concurrency)
        {
            var concurrencyMapping = new YamlMappingNode();
            if (concurrency.ScanConcurrency is { } scanConcurrency)
            {
                concurrencyMapping.Add("scan_concurrency", ToInvariantString(scanConcurrency));
            }

            if (concurrency.TransferConcurrency is { } transferConcurrency)
            {
                concurrencyMapping.Add("transfer_concurrency", ToInvariantString(transferConcurrency));
            }

            mapping.Add("concurrency", concurrencyMapping);
        }

        return mapping;
    }

    private static YamlMappingNode BuildSourceMapping(Source source)
    {
        var mapping = new YamlMappingNode();
        mapping.Add("path", source.Path);
        mapping.Add("recursive", source.Recursive ? "true" : "false");
        AddStringListIfAny(mapping, "exclude", source.Excludes);
        AddStringListIfAny(mapping, "include_globs", source.IncludeGlobs);
        AddStringListIfAny(mapping, "exclude_globs", source.ExcludeGlobs);
        return mapping;
    }

    private static void AddStringListIfAny(YamlMappingNode mapping, string key, IReadOnlyList<string> values)
    {
        if (values.Count == 0)
        {
            return;
        }

        var sequence = new YamlSequenceNode();
        foreach (var value in values)
        {
            sequence.Add(value);
        }

        mapping.Add(key, sequence);
    }

    private static string ToInvariantString(int value) => value.ToString(CultureInfo.InvariantCulture);
}
