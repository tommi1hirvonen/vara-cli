using System.CommandLine;
using Spectre.Console;
using Vara.Application.History;
using Vara.Application.Profiles;
using Vara.Cli.Composition;

namespace Vara.Cli.Commands;

public static class ShowCommand
{
    /// <param name="output">Where a version's content is streamed; defaults to standard output.</param>
    /// <param name="isOutputRedirected">
    /// Whether standard output is redirected (so binary content may be streamed without
    /// refusing); defaults to <see cref="Console.IsOutputRedirected"/>.
    /// </param>
    public static Command Create(
        ProfileResolver profileResolver,
        ProfileServiceFactory serviceFactory,
        Stream? output = null,
        Func<bool>? isOutputRedirected = null,
        IAnsiConsole? errorConsole = null)
    {
        var profileOption = CommandOptions.Profile("query");
        var pathArgument = new Argument<string>("path")
        {
            Description = "The file whose content to show - a mirror-relative path, an absolute source path, " +
                "relative to the current directory."
        };
        var atOption = new Option<string?>("--at")
        {
            Description = "Show the version current as of this date/time " +
                "(for example, '2025-01-15' or '2025-01-15 14:30')."
        };
        var versionOption = new Option<long?>("--version")
        {
            Description = "Show this specific version id (see the 'history' command)."
        };
        var configOption = CommandOptions.Config();
        var forceBinaryOption = new Option<bool>("--force-binary")
        {
            Description = "Stream binary content to an interactive terminal instead of refusing it."
        };

        var command = new Command(
            "show",
            "Stream a specific version's content to standard output, without writing it to disk.")
        {
            pathArgument, profileOption, atOption, versionOption, configOption, forceBinaryOption,
        };

        command.SetAction(parseResult =>
        {
            var profileName = parseResult.GetValue(profileOption);
            var path = parseResult.GetValue(pathArgument)!;
            var at = parseResult.GetValue(atOption);
            var version = parseResult.GetValue(versionOption);
            var configPath = parseResult.GetValue(configOption);
            var forceBinary = parseResult.GetValue(forceBinaryOption);

            if (CommandValidation.ExactlyOne(at is not null, version is not null, "--at <date>", "--version <id>", errorConsole)
                is { } invalid)
            {
                return invalid;
            }

            var resultExitCode = ExitCodes.Success;
            var errorExitCode = ErrorReporting.Run(() =>
            {
                using var session = HistorySession.Open(profileResolver, serviceFactory, profileName, configPath);
                var resolvedPath = session.ResolveFilePath(path);
                var asOf = at is null ? (DateTimeOffset?)null : DateTimeOptionParser.Parse("--at", at);

                var destination = output ?? Console.OpenStandardOutput();
                try
                {
                    var result = session.FileVersions.ShowVersion(
                        resolvedPath,
                        version,
                        asOf,
                        forceBinary: (isOutputRedirected?.Invoke() ?? Console.IsOutputRedirected) || forceBinary);
                    resultExitCode = result switch
                    {
                        Stream stream => WriteVersion(stream, destination),
                        ShowRefusal refusal => ReportRefusal(refusal, errorConsole),
                    };
                }
                finally
                {
                    if (output is null)
                    {
                        destination.Dispose();
                    }
                }
            }, errorConsole);

            return errorExitCode == ExitCodes.Success ? resultExitCode : errorExitCode;
        });

        return command;
    }

    private static int WriteVersion(Stream stream, Stream destination)
    {
        using (stream)
        {
            stream.CopyTo(destination);
        }

        return ExitCodes.Success;
    }

    private static int ReportRefusal(ShowRefusal refusal, IAnsiConsole? errorConsole) =>
        ErrorReporting.Usage(refusal.Message, errorConsole);
}
