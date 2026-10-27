using Microsoft.Extensions.DependencyInjection;
using Vara.Cli.Composition;

// A plain service provider rather than the generic host: the host's service-oriented
// defaults (appsettings.json from the working directory, DOTNET_* environment variables,
// stdout console logging) are not appropriate for a CLI. Add configuration or logging
// explicitly here when needed.
using var services = new ServiceCollection()
    .AddVaraServices()
    .BuildServiceProvider();

// Backs each command's "Graceful cancellation via Ctrl+C" requirement: a single Ctrl+C is
// intercepted (Cancel = true suppresses the OS's default immediate-termination
// behavior) and turned into a cooperative signal a running backup, prune, check, or
// recursive restore can observe, instead of the process being killed with no chance to
// checkpoint its progress. A second Ctrl+C while a graceful stop is still draining forces
// the same abrupt exit Ctrl+C would have caused with no handler at all - see design.md's
// "Second Ctrl+C" decision. Extracted into GracefulCancellation so both code paths are
// unit-testable. One shared token is threaded into every command that needs it - the
// same one Ctrl+C press should be observable by whichever command happens to be running.
var gracefulCancellation = new GracefulCancellation(Environment.Exit);
Console.CancelKeyPress += (_, e) => e.Cancel = gracefulCancellation.RequestCancellation();

var rootCommand = CommandLineComposition.CreateRootCommand(services, gracefulCancellation.TokenSource.Token);

var parseResult = rootCommand.Parse(args);
return parseResult.Invoke();
