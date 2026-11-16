using System.Collections.Concurrent;
using System.Windows.Threading;
using SysMonitor.Services;

namespace SysMonitor.Tests;

public sealed class TaskbarRegionMonitorTests
{
    [Fact]
    public void RecoveryRequestsDoNotInvalidateSlowProbeOrQueuedResult()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var posted = new ConcurrentQueue<Action>();
        var received = new List<TaskbarRegionSnapshot>();
        int probeCount = 0;
        using var monitor = new TaskbarRegionMonitor(
            Dispatcher.CurrentDispatcher,
            received.Add,
            generation =>
            {
                Interlocked.Increment(ref probeCount);
                entered.Set();
                release.Wait(TimeSpan.FromSeconds(5));
                return Snapshot(generation);
            },
            posted.Enqueue);
        try
        {
            monitor.Start();
            Assert.True(entered.Wait(TimeSpan.FromSeconds(2)));
            Parallel.For(0, 1000, _ => monitor.RequestRecoveryProbe());
            release.Set();
            Assert.True(SpinWait.SpinUntil(() => !posted.IsEmpty, TimeSpan.FromSeconds(2)));

            // A result waiting on the UI dispatcher is still pending work.
            monitor.RequestRecoveryProbe();
            Assert.True(posted.TryDequeue(out Action? publish));
            publish!();

            Assert.Equal(1, Volatile.Read(ref probeCount));
            Assert.Equal(1, Assert.Single(received).Generation);
        }
        finally
        {
            release.Set();
        }
    }

    [Fact]
    public void RecoveryAfterPublishedResultSchedulesFreshProbe()
    {
        var posted = new ConcurrentQueue<Action>();
        var received = new List<TaskbarRegionSnapshot>();
        using var monitor = new TaskbarRegionMonitor(
            Dispatcher.CurrentDispatcher, received.Add, Snapshot, posted.Enqueue);
        monitor.Start();
        Assert.True(SpinWait.SpinUntil(() => posted.Count == 1, TimeSpan.FromSeconds(2)));
        Assert.True(posted.TryDequeue(out Action? first));
        first!();
        monitor.RequestRecoveryProbe();
        Assert.True(SpinWait.SpinUntil(() => posted.Count == 1, TimeSpan.FromSeconds(2)));
        Assert.True(posted.TryDequeue(out Action? second));
        second!();

        Assert.Equal(new long[] { 1, 2 }, received.Select(snapshot => snapshot.Generation));
    }

    [Fact]
    public void LayoutChangeStillRejectsOlderQueuedResult()
    {
        var posted = new ConcurrentQueue<Action>();
        var received = new List<TaskbarRegionSnapshot>();
        using var monitor = new TaskbarRegionMonitor(
            Dispatcher.CurrentDispatcher, received.Add, Snapshot, posted.Enqueue);
        monitor.Start();
        Assert.True(SpinWait.SpinUntil(() => posted.Count == 1, TimeSpan.FromSeconds(2)));
        monitor.RequestProbe();
        Assert.True(SpinWait.SpinUntil(() => posted.Count == 2, TimeSpan.FromSeconds(2)));
        while (posted.TryDequeue(out Action? publish))
        {
            publish();
        }

        Assert.Equal(2, Assert.Single(received).Generation);
    }

    [Fact]
    public void StopRejectsQueuedResultAndRecoveryRequestsDoNotRestartWorker()
    {
        var posted = new ConcurrentQueue<Action>();
        var received = new List<TaskbarRegionSnapshot>();
        using var monitor = new TaskbarRegionMonitor(
            Dispatcher.CurrentDispatcher, received.Add, Snapshot, posted.Enqueue);
        monitor.Start();
        Assert.True(SpinWait.SpinUntil(() => posted.Count == 1, TimeSpan.FromSeconds(2)));
        monitor.Stop();
        monitor.RequestRecoveryProbe();
        Assert.True(posted.TryDequeue(out Action? publish));
        publish!();
        Assert.Empty(received);
        Assert.Empty(posted);
    }

    private static TaskbarRegionSnapshot Snapshot(long generation) =>
        new(generation, 1, 0, 1000, 1920, 1040, 500, 1750, 96, true, true);
}
