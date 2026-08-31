using Vara.Application.Backup;
using Vara.Application.Reporting;
using Xunit;

namespace Vara.Application.Tests.Reporting;

public class ProgressHeartbeatTests
{
    [Fact]
    public void Tick_before_any_Update_does_not_invoke_render()
    {
        var heartbeat = new ProgressHeartbeat();
        var invoked = false;

        heartbeat.Tick(_ => invoked = true);

        Assert.False(invoked);
    }

    [Fact]
    public void Tick_after_an_Update_invokes_render_with_the_exact_last_updated_value()
    {
        var heartbeat = new ProgressHeartbeat();
        var progress = new BackupProgress(500, 1000);
        heartbeat.Update(progress);

        BackupProgress? rendered = null;
        heartbeat.Tick(p => rendered = p);

        Assert.Equal(progress, rendered);
    }

    [Fact]
    public void Repeated_ticks_keep_replaying_the_same_last_known_value_until_updated_again()
    {
        var heartbeat = new ProgressHeartbeat();
        heartbeat.Update(new BackupProgress(100, 1000));

        var renders = new List<BackupProgress>();
        heartbeat.Tick(renders.Add);
        heartbeat.Tick(renders.Add);
        heartbeat.Update(new BackupProgress(200, 1000));
        heartbeat.Tick(renders.Add);

        Assert.Equal(
            [new BackupProgress(100, 1000), new BackupProgress(100, 1000), new BackupProgress(200, 1000)],
            renders);
    }

    [Fact]
    public void Update_and_Tick_are_safe_to_call_concurrently_from_different_threads()
    {
        var heartbeat = new ProgressHeartbeat();
        heartbeat.Update(new BackupProgress(0, 1_000_000));
        using var barrier = new ManualResetEventSlim(false);
        var exceptions = new List<Exception>();
        var tickedValues = new System.Collections.Concurrent.ConcurrentBag<long>();

        var updater = new Thread(() =>
        {
            try
            {
                barrier.Wait();
                for (var i = 1; i <= 1000; i++)
                {
                    heartbeat.Update(new BackupProgress(i, 1_000_000));
                }
            }
            catch (Exception ex)
            {
                lock (exceptions) exceptions.Add(ex);
            }
        });

        var ticker = new Thread(() =>
        {
            try
            {
                barrier.Wait();
                for (var i = 0; i < 1000; i++)
                {
                    heartbeat.Tick(p => tickedValues.Add(p.BytesTransferred));
                }
            }
            catch (Exception ex)
            {
                lock (exceptions) exceptions.Add(ex);
            }
        });

        updater.Start();
        ticker.Start();
        barrier.Set();
        updater.Join();
        ticker.Join();

        Assert.Empty(exceptions);
        // Every tick observed some valid, previously-recorded value (never a torn/corrupt
        // read) - the exact interleaving is nondeterministic, so only well-formedness (not a
        // specific sequence) is asserted here.
        Assert.All(tickedValues, v => Assert.InRange(v, 0, 1000));
    }

    [Fact]
    public void Composing_with_a_real_ProgressDisplayGate_admits_a_heartbeat_tick_of_an_unchanged_value_after_its_interval_elapses()
    {
        var current = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var gate = new ProgressDisplayGate(() => current);
        var heartbeat = new ProgressHeartbeat();
        var rendered = new List<long>();

        var initial = new BackupProgress(100, 1000);
        heartbeat.Update(initial);
        gate.Report(initial, p => rendered.Add(p.BytesTransferred));

        // No further Update - simulates a stalled transfer. Advance time past the gate's
        // own redraw interval, then tick the heartbeat with the same (unchanged) value.
        current = current.AddMilliseconds(200);
        heartbeat.Tick(p => gate.Report(p, admitted => rendered.Add(admitted.BytesTransferred)));

        Assert.Equal([100L, 100L], rendered);
    }
}
