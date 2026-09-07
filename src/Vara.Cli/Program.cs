using System.CommandLine;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Vara.Application.Profiles;
using Vara.Cli.Commands;
using Vara.Cli.Composition;
using Vara.Core.Abstractions;
using Vara.Infrastructure.Configuration;
using Vara.Infrastructure.FileSystem;
using Vara.Infrastructure.Hashing;

var builder = Host.CreateApplicationBuilder();
builder.Services.AddSingleton<IProfileConfigLoader, YamlProfileConfigLoader>();
builder.Services.AddSingleton<IHasher, XxHash128Hasher>();
builder.Services.AddSingleton<IFileSystemScanner, DirectoryFileSystemScanner>();
builder.Services.AddSingleton<ProfileResolver>();
builder.Services.AddSingleton<ProfileServiceFactory>();

using var host = builder.Build();
var services = host.Services;

var profileResolver = services.GetRequiredService<ProfileResolver>();
var serviceFactory = services.GetRequiredService<ProfileServiceFactory>();
var scanner = services.GetRequiredService<IFileSystemScanner>();
var hasher = services.GetRequiredService<IHasher>();

// Backs the "Graceful cancellation via Ctrl+C" requirement: a single Ctrl+C is
// intercepted (Cancel = true suppresses the OS's default immediate-termination
// behavior) and turned into a cooperative signal a running backup can observe,
// instead of the process being killed with no chance to checkpoint its progress. A
// second Ctrl+C while a graceful stop is still draining forces the same abrupt exit
// Ctrl+C would have caused with no handler at all - see design.md's "Second Ctrl+C"
// decision. Extracted into GracefulCancellation so both code paths are unit-testable.
var gracefulCancellation = new GracefulCancellation(Environment.Exit);
Console.CancelKeyPress += (_, e) => e.Cancel = gracefulCancellation.RequestCancellation();

var rootCommand = new RootCommand("Vara - versioned, deduplicated backup tool")
{
    BackupCommand.Create(profileResolver, serviceFactory, scanner, hasher, gracefulCancellation.TokenSource.Token),
    SnapshotsCommand.Create(profileResolver, serviceFactory),
    HistoryCommand.Create(profileResolver, serviceFactory),
    RestoreCommand.Create(profileResolver, serviceFactory),
    BrowseCommand.Create(profileResolver, serviceFactory),
    DeletedCommand.Create(profileResolver, serviceFactory),
    ShowCommand.Create(profileResolver, serviceFactory),
    DiffCommand.Create(profileResolver, serviceFactory),
    PruneCommand.Create(profileResolver, serviceFactory),
    CheckCommand.Create(profileResolver, serviceFactory, hasher),
};

var parseResult = rootCommand.Parse(args);
return parseResult.Invoke();
