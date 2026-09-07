using Microsoft.Data.Sqlite;
using Spectre.Console.Testing;
using Vara.Application.History;
using Vara.Cli.Presentation;
using Vara.Core.Snapshots;
using Vara.Infrastructure.Snapshots;
using Xunit;

namespace Vara.Cli.Tests.Presentation;

/// <summary>
/// End-to-end check that a <c>Cancelled</c> snapshot recorded by a real
/// <see cref="SqliteSnapshotRepository"/> - via <see cref="SnapshotHistoryService"/>, the
/// same path <c>vara snapshots</c> uses - renders distinguishably from <c>Failed</c> and
/// <c>Complete</c> rows, per snapshot-history's "Cancelled run is distinguished from a
/// failed run" scenario. <see cref="SnapshotsTablePresenterTests"/> covers the presenter
/// in isolation against hand-built <see cref="Snapshot"/> records; this test instead goes
/// through the real repository and history service to catch any drift in how the status
/// actually round-trips end-to-end.
/// </summary>
public class SnapshotsTablePresenterRealRepositoryTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"vara-clitest-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath))
        {
            File.Delete(_dbPath);
        }
    }

    [Fact]
    public void A_Cancelled_snapshot_renders_in_the_warning_style_alongside_Complete_and_Failed_rows()
    {
        using (var repository = new SqliteSnapshotRepository(_dbPath))
        {
            var completeId = repository.BeginSnapshot(DateTimeOffset.UnixEpoch);
            repository.CompleteSnapshot(completeId, DateTimeOffset.UnixEpoch, SnapshotStats.Empty);

            var failedId = repository.BeginSnapshot(DateTimeOffset.UnixEpoch);
            repository.FailSnapshot(failedId, DateTimeOffset.UnixEpoch, SnapshotStats.Empty);

            var cancelledId = repository.BeginSnapshot(DateTimeOffset.UnixEpoch);
            repository.CancelSnapshot(cancelledId, DateTimeOffset.UnixEpoch, SnapshotStats.Empty);
        }

        using var reopened = new SqliteSnapshotRepository(_dbPath);
        var history = new SnapshotHistoryService(reopened, contentStore: null!);
        var snapshots = history.ListSnapshots();

        var console = new TestConsole { EmitAnsiSequences = true };
        console.Profile.Capabilities.Ansi = true;
        console.Profile.Capabilities.ColorSystem = Spectre.Console.ColorSystem.EightBit;

        SnapshotsTablePresenter.Render(console, snapshots);

        Assert.Contains("Complete", console.Output);
        Assert.Contains("Failed", console.Output);
        Assert.Contains("Cancelled", console.Output);
        Assert.Contains("\u001b[1;38;5;121m", console.Output); // bold PaleGreen1 - Complete
        Assert.Contains("\u001b[1;38;5;131m", console.Output); // bold IndianRed - Failed
        Assert.Contains("\u001b[1;38;5;186m", console.Output); // bold LightGoldenrod2 - Cancelled (warning, distinct from Failed's hard-error style)
    }
}
