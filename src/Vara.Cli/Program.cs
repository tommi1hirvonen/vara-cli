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

var rootCommand = new RootCommand("Vara - versioned, deduplicated backup tool")
{
    BackupCommand.Create(profileResolver, serviceFactory, scanner, hasher),
    SnapshotsCommand.Create(profileResolver, serviceFactory),
    HistoryCommand.Create(profileResolver, serviceFactory),
    RestoreCommand.Create(profileResolver, serviceFactory),
    PruneCommand.Create(profileResolver, serviceFactory),
};

var parseResult = rootCommand.Parse(args);
return parseResult.Invoke();
