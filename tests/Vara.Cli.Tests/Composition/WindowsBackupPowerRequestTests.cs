using Vara.Cli.Composition;
using Xunit;

namespace Vara.Cli.Tests.Composition;

public class WindowsBackupPowerRequestTests
{
    [Fact]
    public void Request_creation_activation_release_and_handle_close_follow_expected_order()
    {
        var api = new FakeWindowsPowerRequestApi();
        var request = new WindowsBackupPowerRequest(api);

        Assert.Equal(["create:Vara backup in progress", "set:42"], api.Calls);

        request.Dispose();

        Assert.Equal(
            ["create:Vara backup in progress", "set:42", "clear:42", "close:42"],
            api.Calls);
    }

    [Fact]
    public void Dispose_is_idempotent()
    {
        var api = new FakeWindowsPowerRequestApi();
        var request = new WindowsBackupPowerRequest(api);

        request.Dispose();
        request.Dispose();

        Assert.Equal(1, api.Calls.Count(call => call == "clear:42"));
        Assert.Equal(1, api.Calls.Count(call => call == "close:42"));
    }

    [Fact]
    public void Activation_failure_closes_the_created_handle()
    {
        var api = new FakeWindowsPowerRequestApi
        {
            SetFailure = new PowerRequestException("activation failed"),
        };

        var exception = Assert.Throws<PowerRequestException>(() => new WindowsBackupPowerRequest(api));

        Assert.Contains("activation failed", exception.Message);
        Assert.Equal(
            ["create:Vara backup in progress", "set:42", "close:42"],
            api.Calls);
    }

    [Fact]
    public void Activation_and_handle_close_failures_are_both_preserved()
    {
        var api = new FakeWindowsPowerRequestApi
        {
            SetFailure = new PowerRequestException("activation failed"),
            CloseFailure = new PowerRequestException("close failed"),
        };

        var exception = Assert.Throws<PowerRequestException>(() => new WindowsBackupPowerRequest(api));

        Assert.Contains("activation failed", exception.Message);
        Assert.Contains("close failed", exception.Message);
        Assert.Equal(
            ["create:Vara backup in progress", "set:42", "close:42"],
            api.Calls);
    }

    [Fact]
    public void Clear_failure_still_closes_handle_and_is_reported()
    {
        var api = new FakeWindowsPowerRequestApi
        {
            ClearFailure = new PowerRequestException("clear failed"),
        };
        var request = new WindowsBackupPowerRequest(api);

        var exception = Assert.Throws<PowerRequestException>(request.Dispose);

        Assert.Contains("clear failed", exception.Message);
        Assert.Equal(
            ["create:Vara backup in progress", "set:42", "clear:42", "close:42"],
            api.Calls);
    }

    private sealed class FakeWindowsPowerRequestApi : IWindowsPowerRequestApi
    {
        public List<string> Calls { get; } = [];
        public PowerRequestException? SetFailure { get; init; }
        public PowerRequestException? ClearFailure { get; init; }
        public PowerRequestException? CloseFailure { get; init; }

        public nint Create(string reason)
        {
            Calls.Add($"create:{reason}");
            return 42;
        }

        public void SetSystemRequired(nint handle)
        {
            Calls.Add($"set:{handle}");
            if (SetFailure is not null)
            {
                throw SetFailure;
            }
        }

        public void ClearSystemRequired(nint handle)
        {
            Calls.Add($"clear:{handle}");
            if (ClearFailure is not null)
            {
                throw ClearFailure;
            }
        }

        public void Close(nint handle)
        {
            Calls.Add($"close:{handle}");
            if (CloseFailure is not null)
            {
                throw CloseFailure;
            }
        }
    }
}
