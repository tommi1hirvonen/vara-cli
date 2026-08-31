using Vara.Application.Backup;
using Vara.Application.Reporting;
using Xunit;

namespace Vara.Application.Tests.Reporting;

/// <summary>
/// End-to-end simulation of a stalled transfer across <see cref="ProgressHeartbeat"/>,
/// <see cref="ProgressDisplayGate"/>, and <see cref="BackupProgressCalculator"/> together -
/// the same three components <c>BackupCommand</c> wires up - using injected <c>nowProvider</c>s
/// so the "stall" is simulated deterministically rather than by a real wall-clock wait.
/// </summary>
public class ProgressHeartbeatStallSimulationTests
{
    [Fact]
    public void A_heartbeat_tick_during_a_simulated_stall_lowers_throughput_and_lengthens_eta()
    {
        var current = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        DateTimeOffset Now() => current;

        var calculator = new BackupProgressCalculator(Now);
        var gate = new ProgressDisplayGate(Now);
        var heartbeat = new ProgressHeartbeat();

        ProgressSnapshot? lastSnapshot = null;
        void Render(BackupProgress admitted) => lastSnapshot = calculator.Calculate(admitted);

        // Establish a steady transfer: 1000 bytes/sec for the first 2 seconds of a 10,000-byte run.
        var progress = new BackupProgress(0, 10_000);
        heartbeat.Update(progress);
        gate.Report(progress, Render);

        current = current.AddSeconds(2);
        progress = new BackupProgress(2000, 10_000);
        heartbeat.Update(progress);
        gate.Report(progress, Render);

        var throughputBeforeStall = lastSnapshot!.ThroughputBytesPerSecond;
        var etaBeforeStall = lastSnapshot!.EstimatedTimeRemaining;
        Assert.Equal(1000, throughputBeforeStall, precision: 3);

        // Simulate a stall: no further Update (no new bytes reported) for a long time, then
        // the heartbeat ticks - exactly what a real System.Threading.Timer would do.
        current = current.AddSeconds(18); // 20s elapsed total, still only 2000 bytes transferred
        heartbeat.Tick(p => gate.Report(p, Render));

        Assert.NotNull(lastSnapshot);
        Assert.Equal(2000, lastSnapshot!.BytesTransferred); // byte count itself is unchanged...
        Assert.True(
            lastSnapshot.ThroughputBytesPerSecond < throughputBeforeStall,
            $"expected throughput to drop below {throughputBeforeStall}, was {lastSnapshot.ThroughputBytesPerSecond}");
        Assert.True(
            lastSnapshot.EstimatedTimeRemaining > etaBeforeStall,
            $"expected ETA to grow beyond {etaBeforeStall}, was {lastSnapshot.EstimatedTimeRemaining}");
    }

    [Fact]
    public void Resuming_after_a_heartbeat_tick_reports_progress_normally_again()
    {
        var current = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        DateTimeOffset Now() => current;

        var calculator = new BackupProgressCalculator(Now);
        var gate = new ProgressDisplayGate(Now);
        var heartbeat = new ProgressHeartbeat();

        var rendered = new List<long>();
        void Render(BackupProgress admitted)
        {
            rendered.Add(admitted.BytesTransferred);
            calculator.Calculate(admitted);
        }

        var progress = new BackupProgress(100, 1000);
        heartbeat.Update(progress);
        gate.Report(progress, Render);

        // Stall: heartbeat replays the unchanged value once.
        current = current.AddMilliseconds(200);
        heartbeat.Tick(p => gate.Report(p, Render));

        // Resumes: a real new, higher value is reported again.
        current = current.AddMilliseconds(200);
        progress = new BackupProgress(400, 1000);
        heartbeat.Update(progress);
        gate.Report(progress, Render);

        Assert.Equal([100L, 100L, 400L], rendered);
    }
}
