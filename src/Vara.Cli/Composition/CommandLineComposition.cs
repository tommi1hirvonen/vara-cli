using System.CommandLine;
using Microsoft.Extensions.DependencyInjection;
using Vara.Application.Profiles;
using Vara.Cli.Commands;
using Vara.Core.Abstractions;
using Vara.Infrastructure.Configuration;
using Vara.Infrastructure.FileSystem;
using Vara.Infrastructure.Hashing;

namespace Vara.Cli.Composition;

/// <summary>
/// The composition root's two testable halves: which services the CLI registers, and how
/// the command tree is assembled from them. <c>Program.cs</c> only adds process-level concerns
/// (the host, Ctrl+C handling, and invoking the parsed command).
/// </summary>
public static class CommandLineComposition
{
    public static IServiceCollection AddVaraServices(this IServiceCollection services) => services
        .AddSingleton<IProfileConfigLoader, YamlProfileConfigLoader>()
        .AddSingleton<IProfileConfigWriter, YamlProfileConfigWriter>()
        .AddSingleton<IHasher, XxHash128Hasher>()
        .AddSingleton<IFileSystemScanner, DirectoryFileSystemScanner>()
        .AddSingleton<ProfileResolver>()
        .AddSingleton<ProfileServiceFactory>();

    /// <param name="cancellationToken">
    /// The one shared token every long-running command (backup, prune, check, recursive
    /// restore, profiles) observes, so a single Ctrl+C is visible to whichever one is running.
    /// </param>
    public static RootCommand CreateRootCommand(IServiceProvider services, CancellationToken cancellationToken)
    {
        var profileResolver = services.GetRequiredService<ProfileResolver>();
        var serviceFactory = services.GetRequiredService<ProfileServiceFactory>();
        var scanner = services.GetRequiredService<IFileSystemScanner>();
        var hasher = services.GetRequiredService<IHasher>();
        var configLoader = services.GetRequiredService<IProfileConfigLoader>();
        var configWriter = services.GetRequiredService<IProfileConfigWriter>();

        return
        [
            with("Vara - versioned, deduplicated backup tool"),
            BackupCommand.Create(profileResolver, serviceFactory, scanner, hasher, cancellationToken: cancellationToken),
            SnapshotsCommand.Create(profileResolver, serviceFactory),
            SnapshotCommand.Create(profileResolver, serviceFactory),
            HistoryCommand.Create(profileResolver, serviceFactory),
            RestoreCommand.Create(profileResolver, serviceFactory, cancellationToken),
            BrowseCommand.Create(profileResolver, serviceFactory),
            DeletedCommand.Create(profileResolver, serviceFactory),
            ShowCommand.Create(profileResolver, serviceFactory),
            DiffCommand.Create(profileResolver, serviceFactory),
            PruneCommand.Create(profileResolver, serviceFactory, cancellationToken),
            CheckCommand.Create(profileResolver, serviceFactory, hasher, cancellationToken: cancellationToken),
            ProfilesCommand.Create(configLoader, configWriter, cancellationToken),
        ];
    }
}
