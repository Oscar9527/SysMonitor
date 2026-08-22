using System.Collections.Concurrent;
using System.Windows.Threading;
using SysMonitor.Services;

namespace SysMonitor.Tests;

public sealed class TaskbarMotionTrackerTests
{
    [Fact]
    public void AcceptedEventStormRetainsOnePostedCallbackAndSchedulesOneProbe()
    {
        Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
        var posted = new ConcurrentQueue<Action>();
        var counters = new Counters();
        using var tracker = CreateTracker(
            dispatcher,
            posted,
            counters);

        tracker.ActivateForTesting(generation: 7);
        Parallel.For(0, 1000, _ => tracker.NotifyAcceptedLayoutChangeForTesting());

        Assert.Equal(1, Volatile.Read(ref counters.PostCount));
        Assert.True(posted.TryDequeue(out Action? callback));
        Assert.Empty(posted);

        callback!();
        tracker.RunEventDebounceForTesting();

        Assert.Equal(1, Volatile.Read(ref counters.ProbeCount));
    }

    [Fact]
    public void DisposeBeforePostedCallbackRunsDoesNotProbe()
    {
        Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
        var posted = new ConcurrentQueue<Action>();
        var counters = new Counters();
        using var tracker = CreateTracker(
            dispatcher,
            posted,
            counters);

        tracker.ActivateForTesting(generation: 11);
        tracker.NotifyAcceptedLayoutChangeForTesting();
        Assert.Equal(1, Volatile.Read(ref counters.PostCount));
        Assert.True(posted.TryDequeue(out Action? callback));

        tracker.Dispose();
        callback!();

        Assert.Equal(0, Volatile.Read(ref counters.ProbeCount));
    }

    private static TaskbarMotionTracker CreateTracker(
        Dispatcher dispatcher,
        ConcurrentQueue<Action> posted,
        Counters counters) =>
        new(
            dispatcher,
            reposition: static () => { },
            requestRegionProbe: () => Interlocked.Increment(ref counters.ProbeCount),
            requestRecoveryProbe: static () => { },
            healthCheck: static () => { },
            postDispatcherNotification: callback =>
            {
                Interlocked.Increment(ref counters.PostCount);
                posted.Enqueue(callback);
            });

    private sealed class Counters
    {
        public int PostCount;
        public int ProbeCount;
    }
}
