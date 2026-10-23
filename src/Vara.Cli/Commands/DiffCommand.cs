using System.CommandLine;
using DiffPlex;
using DiffPlex.DiffBuilder;
using DiffPlex.DiffBuilder.Model;
using Spectre.Console;
using Vara.Application.History;
using Vara.Application.Profiles;
using Vara.Cli.Composition;

namespace Vara.Cli.Commands;

public static class DiffCommand
{
    public static Command Create(
        ProfileResolver profileResolver,
        ProfileServiceFactory serviceFactory,
        IAnsiConsole? console = null,
        IAnsiConsole? errorConsole = null)
    {
        var ansiConsole = console ?? AnsiConsole.Console;
        var profileOption = CommandOptions.Profile("query");
        var pathArgument = new Argument<string>("path")
        {
            Description = "The file whose versions to diff - a mirror-relative path, an absolute source path, " +
                "relative to the current directory."
        };
        var leftAtOption = new Option<string?>("--left-at")
        {
            Description = "The earlier side of the diff: the version current as of this date/time " +
                "(for example, '2025-01-15' or '2025-01-15 14:30')."
        };
        var leftVersionOption = new Option<long?>("--left-version")
        {
            Description = "The earlier side of the diff: this specific version id."
        };
        var rightAtOption = new Option<string?>("--right-at")
        {
            Description = "The later side of the diff: the version current as of this date/time " +
                "(for example, '2025-01-15' or '2025-01-15 14:30')."
        };
        var rightVersionOption = new Option<long?>("--right-version")
        {
            Description = "The later side of the diff: this specific version id."
        };
        var configOption = CommandOptions.Config();

        var command = new Command("diff", "Show a textual diff between two versions of the same file.")
        {
            pathArgument, profileOption, leftAtOption, leftVersionOption,
            rightAtOption, rightVersionOption, configOption,
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

            if (CommandValidation.ExactlyOne(
                    leftAt is not null, leftVersion is not null, "--left-at <date>", "--left-version <id>", errorConsole)
                is { } invalidLeft)
            {
                return invalidLeft;
            }

            if (CommandValidation.ExactlyOne(
                    rightAt is not null, rightVersion is not null, "--right-at <date>", "--right-version <id>", errorConsole)
                is { } invalidRight)
            {
                return invalidRight;
            }

            var resultExitCode = ExitCodes.Success;
            var errorExitCode = ErrorReporting.Run(() =>
            {
                using var session = HistorySession.Open(profileResolver, serviceFactory, profileName, configPath);
                var resolvedPath = session.ResolveFilePath(path);

                var leftAsOf = leftAt is null
                    ? (DateTimeOffset?)null
                    : DateTimeOptionParser.Parse("--left-at", leftAt);
                var rightAsOf = rightAt is null
                    ? (DateTimeOffset?)null
                    : DateTimeOptionParser.Parse("--right-at", rightAt);

                var result = session.FileVersions.OpenVersionsForDiff(
                    resolvedPath, leftVersion, leftAsOf, rightVersion, rightAsOf);
                resultExitCode = result switch
                {
                    DiffStreams streams => RenderDiffStreams(streams, ansiConsole),
                    DiffRefusal refusal => ErrorReporting.Usage(refusal.Message, errorConsole),
                };
            }, errorConsole);

            return errorExitCode == ExitCodes.Success ? resultExitCode : errorExitCode;
        });

        return command;
    }

    private static int RenderDiffStreams(DiffStreams streams, IAnsiConsole console)
    {
        using (streams)
        using (var leftReader = new StreamReader(streams.Left, leaveOpen: true))
        using (var rightReader = new StreamReader(streams.Right, leaveOpen: true))
        {
            RenderDiff(console, leftReader.ReadToEnd(), rightReader.ReadToEnd());
        }

        return ExitCodes.Success;
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
