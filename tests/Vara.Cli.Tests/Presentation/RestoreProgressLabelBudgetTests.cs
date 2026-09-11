using Vara.Cli.Presentation;
using Xunit;

namespace Vara.Cli.Tests.Presentation;

public class RestoreProgressLabelBudgetTests
{
    [Fact]
    public void Wide_terminal_yields_a_generous_budget_above_the_minimum_floor()
    {
        var budget = RestoreProgressLabelBudget.Compute(200);

        Assert.True(budget > 20);
    }

    [Fact]
    public void Narrow_terminal_still_yields_at_least_the_minimum_floor()
    {
        var budget = RestoreProgressLabelBudget.Compute(50);

        Assert.True(budget >= 20);
    }

    [Fact]
    public void Extremely_narrow_terminal_is_clamped_to_the_minimum_floor()
    {
        var budget = RestoreProgressLabelBudget.Compute(1);

        Assert.Equal(20, budget);
    }
}
