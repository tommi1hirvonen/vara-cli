using Microsoft.Extensions.DependencyInjection;
using Vara.Cli.Composition;
using Xunit;

namespace Vara.Cli.Tests.Composition;

public class CommandLineCompositionTests
{
    private static ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddVaraServices();
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }

    [Fact]
    public void Every_registered_service_can_be_resolved()
    {
        using var provider = BuildProvider();

        var root = CommandLineComposition.CreateRootCommand(provider, CancellationToken.None);

        Assert.NotNull(root);
    }

    [Fact]
    public void The_root_command_exposes_every_documented_subcommand_exactly_once()
    {
        using var provider = BuildProvider();

        var root = CommandLineComposition.CreateRootCommand(provider, CancellationToken.None);

        var names = root.Subcommands.Select(c => c.Name).Order().ToList();
        Assert.Equal(
            ["backup", "browse", "check", "deleted", "diff", "history", "profiles", "prune", "restore", "show", "snapshot", "snapshots"],
            names);
    }

    [Fact]
    public void Every_subcommand_has_a_description_for_help_output()
    {
        using var provider = BuildProvider();

        var root = CommandLineComposition.CreateRootCommand(provider, CancellationToken.None);

        Assert.All(root.Subcommands, c => Assert.False(string.IsNullOrWhiteSpace(c.Description), c.Name));
    }

    [Fact]
    public void An_unknown_command_is_a_parse_error()
    {
        using var provider = BuildProvider();
        var root = CommandLineComposition.CreateRootCommand(provider, CancellationToken.None);

        var parse = root.Parse(["no-such-command"]);

        Assert.NotEmpty(parse.Errors);
    }

    [Fact]
    public void Parsing_help_for_a_subcommand_succeeds_without_touching_any_profile()
    {
        using var provider = BuildProvider();
        var root = CommandLineComposition.CreateRootCommand(provider, CancellationToken.None);

        var exitCode = root.Parse(["history", "--help"]).Invoke();

        Assert.Equal(0, exitCode);
    }
}
