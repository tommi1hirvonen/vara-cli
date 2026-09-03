using System.CommandLine;
using System.Globalization;
using DiffPlex;
using DiffPlex.DiffBuilder;
using DiffPlex.DiffBuilder.Model;
using Spectre.Console;
using Vara.Application.History;
using Vara.Application.Profiles;
using Vara.Cli.Composition;
using Vara.Cli.Presentation;

namespace Vara.Cli.Commands;

public static class DiffCommand
{
    public static Command Create(ProfileResolver profileResolver, ProfileServiceFactory serviceFactory)
    {
        var profileOption = new Option<string?>("--profile") { Description = "The profile to query. Optional when the current directory is inside a profile's target root." };
        var pathArgument = new Argument<string>("path") { Description = "The file whose versions to diff - a mirror-relative path, an absolute source path, or a path relative to the current directory." };
        var leftAtOption = new Option<string?>("--left-at") { Description = "The earlier side of the diff: the version current as of this date/time." };
        var leftVersionOption = new Option<long?>("--left-version") { Description = "The earlier side of the diff: this specific version id." };
        var rightAtOption = new Option<string?>("--right-at") { Description = "The later side of the diff: the version current as of this date/time." };
        var rightVersionOption = new Option<long?>("--right-version") { Description = "The later side of the diff: this specific version id." };
        var configOption = new Option<string?>("--config") { Description = "Path to the profiles configuration file (default: ~/.vara/profiles.yml)." };

        var command = new Command("diff", "Show a textual diff between two versions of the same file.")
        {
            pathArgument, profileOption, leftAtOption, leftVersionOption, rightAtOption, rightVersionOption, configOption,
        };

        command.SetAction(parseResult =>
        {
            var profileName = parseResult.GetValue(profileOption);
            var path = parseResult.GetValue(pathArgument)!;
            var leftAt = parseResult.GetValue(leftAtOption);
            var leftVersion = parseResult.GetValue(leftVersionOption);
            var rightAt = parseResult.GetValue(rightAtOption);
            var rightVersion = parseResult.GetValue(rightVersionOption);
            var configPath = parseResult.GetValue(configOption);

            if (leftAt is null && leftVersion is null)
            {
                OutcomeStyle.WriteLineError(StandardError.Console, "Error: specify either --left-at <date> or --left-version <id>.");
                return 1;
            }

            if (rightAt is null && rightVersion is null)
            {
                OutcomeStyle.WriteLineError(StandardError.Console, "Error: specify either --right-at <date> or --right-version <id>.");
                return 1;
            }

            return ErrorReporting.Run(() =>
            {
                var profile = profileResolver.ResolveForBrowsing(profileName, configPath);
                using var services = serviceFactory.CreateFor(profile);
                var history = new SnapshotHistoryService(services.Repository, services.ContentStore);

                var resolvedPath = SnapshotPathResolver.TryResolve(
                    profile.TargetRoot,
                    path,
                    candidate => services.Repository.GetFileHistory(candidate).Count > 0,
                    out var candidatePath)
                    ? candidatePath
                    : path;

                var leftAsOf = leftAt is null ? (DateTimeOffset?)null : DateTimeOffset.Parse(leftAt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal);
                var rightAsOf = rightAt is null ? (DateTimeOffset?)null : DateTimeOffset.Parse(rightAt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal);

                var (left, right) = history.OpenVersionsForDiff(resolvedPath, leftVersion, leftAsOf, rightVersion, rightAsOf);
                using (left)
                using (right)
                {
                    var leftText = new StreamReader(left).ReadToEnd();
                    var rightText = new StreamReader(right).ReadToEnd();

                    RenderDiff(AnsiConsole.Console, leftText, rightText);
                }
            });
        });

        return command;
    }

    private static void RenderDiff(IAnsiConsole console, string leftText, string rightText)
    {
        var diff = new InlineDiffBuilder(new Differ()).BuildDiffModel(leftText, rightText);

        foreach (var line in diff.Lines)
        {
            var (prefix, style) = line.Type switch
            {
                ChangeType.Inserted => ("+ ", new Style(Color.PaleGreen1)),
                ChangeType.Deleted => ("- ", new Style(Color.IndianRed)),
                _ => ("  ", Style.Plain),
            };

            console.WriteLine(prefix + line.Text, style);
        }
    }
}
