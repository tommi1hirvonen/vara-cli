using Vara.Cli.Composition;
using Xunit;

namespace Vara.Cli.Tests.Composition;

public class GracefulCancellationTests
{
    [Fact]
    public void First_request_cancels_the_token_and_returns_true_without_forcing_exit()
    {
        var forceExitCalls = new List<int>();
        var cancellation = new GracefulCancellation(forceExitCalls.Add);

        var suppressDefaultTermination = cancellation.RequestCancellation();

        Assert.True(suppressDefaultTermination);
        Assert.True(cancellation.TokenSource.IsCancellationRequested);
        Assert.Empty(forceExitCalls);
    }

    [Fact]
    public void Second_request_forces_an_immediate_exit_with_the_hard_error_code_instead_of_cancelling_again()
    {
        var forceExitCalls = new List<int>();
        var cancellation = new GracefulCancellation(forceExitCalls.Add);

        cancellation.RequestCancellation(); // first press: graceful stop requested
        var suppressDefaultTermination = cancellation.RequestCancellation(); // second press: force exit

        Assert.False(suppressDefaultTermination);
        Assert.Equal([ExitCodes.HardError], forceExitCalls);
    }
}
