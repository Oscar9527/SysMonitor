using System.Collections.Concurrent;
using System.Diagnostics;
using SysMonitor.Services;

namespace SysMonitor.Tests;

public sealed class UiRefreshSchedulerTests
{
    [Fact]
    public async Task StrictModeHonorsMinimumIntervalAndUsesLatestCallback()
    {
        TimeSpan interval = TimeSpan.FromMilliseconds(100);
        var first = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var scheduler = new UiRefreshScheduler(
            dispatch: action => action(),
            isActive: () => true,
            callback: () => { },
            interval: interval,
            enforceMinimumInterval: true);

        scheduler.Request(() => first.TrySetResult(Stopwatch.GetTimestamp()));
        long firstStarted = await first.Task.WaitAsync(TimeSpan.FromSeconds(2));

        scheduler.Request(() => throw new InvalidOperationException("Replaced callback must not run."));
        scheduler.Request(() => second.TrySetResult(Stopwatch.GetTimestamp()));
        long secondStarted = await second.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(
            Stopwatch.GetElapsedTime(firstStarted, secondStarted) >= interval,
            "Strict refresh callbacks started closer together than the configured interval.");
    }

    [Fact]
    public async Task StrictModeSetIntervalReschedulesPendingWorkEarlierAndLater()
    {
        var first = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        var shortened = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        var lengthened = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var scheduler = new UiRefreshScheduler(
            dispatch: action => action(),
            isActive: () => true,
            callback: () => { },
            interval: TimeSpan.FromMilliseconds(500),
            enforceMinimumInterval: true);

        scheduler.Request(() => first.TrySetResult(Stopwatch.GetTimestamp()));
        await first.Task.WaitAsync(TimeSpan.FromSeconds(2));
        scheduler.Request(() => shortened.TrySetResult(Stopwatch.GetTimestamp()));
        await Task.Delay(40);
        scheduler.SetInterval(TimeSpan.FromMilliseconds(20));
        await shortened.Task.WaitAsync(TimeSpan.FromMilliseconds(300));

        scheduler.Request(() => lengthened.TrySetResult(Stopwatch.GetTimestamp()));
        scheduler.SetInterval(TimeSpan.FromMilliseconds(180));
        await Task.Delay(80);
        Assert.False(lengthened.Task.IsCompleted);
        await lengthened.Task.WaitAsync(TimeSpan.FromMilliseconds(500));
    }

    [Fact]
    public async Task InvalidatePendingRejectsQueuedWorkWithoutConsumingNewRequest()
    {
        var queued = new ConcurrentQueue<Action>();
        int callbackValue = 0;
        using var scheduler = new UiRefreshScheduler(
            dispatch: queued.Enqueue,
            isActive: () => true,
            callback: () => { });

        scheduler.Request(() => callbackValue = 1);
        await WaitUntilAsync(() => queued.Count == 1);
        scheduler.InvalidatePending();
        scheduler.Request(() => callbackValue = 2);
        await WaitUntilAsync(() => queued.Count == 2);

        Assert.True(queued.TryDequeue(out Action? obsolete));
        obsolete();
        Assert.Equal(0, callbackValue);

        Assert.True(queued.TryDequeue(out Action? current));
        current();
        Assert.Equal(2, callbackValue);
    }

    [Fact]
    public async Task RestartIntervalPreservesPendingWorkAndMovesItsBoundary()
    {
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var scheduler = new UiRefreshScheduler(
            dispatch: action => action(),
            isActive: () => true,
            callback: () => { },
            interval: TimeSpan.FromMilliseconds(150),
            enforceMinimumInterval: true);

        scheduler.Request(() => first.TrySetResult());
        await first.Task.WaitAsync(TimeSpan.FromSeconds(2));
        scheduler.Request(() => pending.TrySetResult());
        await Task.Delay(50);
        scheduler.RestartInterval();

        await Task.Delay(80);
        Assert.False(pending.Task.IsCompleted);
        await pending.Task.WaitAsync(TimeSpan.FromMilliseconds(300));
    }

    [Fact]
    public async Task CoalescesBurstsAndDeliversTrailingRequest()
    {
        var queued = new Queue<Action>();
        var dispatched = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int callbackCount = 0;
        UiRefreshScheduler? scheduler = null;
        scheduler = new UiRefreshScheduler(
            dispatch: action =>
            {
                lock (queued)
                {
                    queued.Enqueue(action);
                }

                dispatched.TrySetResult();
            },
            isActive: () => true,
            callback: () =>
            {
                Interlocked.Increment(ref callbackCount);
                scheduler!.Request();
            },
            interval: TimeSpan.FromMilliseconds(10));

        try
        {
            scheduler.Request();
            scheduler.Request();
            scheduler.Request();
            await dispatched.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Action first;
            lock (queued) first = queued.Dequeue();
            first();
            Assert.Equal(1, callbackCount);

            await Task.Delay(40);
            Action trailing;
            lock (queued) trailing = queued.Dequeue();
            trailing();
            Assert.Equal(2, callbackCount);
        }
        finally
        {
            scheduler.Dispose();
        }
    }

    [Fact]
    public void DisposeDropsQueuedCallback()
    {
        Action? queued = null;
        using var dispatched = new ManualResetEventSlim();
        int callbackCount = 0;
        using var scheduler = new UiRefreshScheduler(
            dispatch: action =>
            {
                queued = action;
                dispatched.Set();
            },
            isActive: () => true,
            callback: () => Interlocked.Increment(ref callbackCount));

        scheduler.Request();
        Assert.True(dispatched.Wait(TimeSpan.FromSeconds(2)));
        scheduler.Dispose();
        queued!.Invoke();
        Assert.Equal(0, callbackCount);
    }

    [Fact]
    public async Task DisposeWaitsForInFlightCallbackAndPreventsLaterCallbacks()
    {
        Action? queued = null;
        using var dispatched = new ManualResetEventSlim();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        int callbackCount = 0;
        var scheduler = new UiRefreshScheduler(
            dispatch: action =>
            {
                queued = action;
                dispatched.Set();
            },
            isActive: () => true,
            callback: () =>
            {
                entered.Set();
                release.Wait(TimeSpan.FromSeconds(2));
                Interlocked.Increment(ref callbackCount);
            });

        scheduler.Request();
        Assert.True(dispatched.Wait(TimeSpan.FromSeconds(2)));
        Task callback = Task.Run(queued!);
        Assert.True(entered.Wait(TimeSpan.FromSeconds(2)));
        Task dispose = Task.Run(scheduler.Dispose);
        await Task.Delay(50);
        Assert.False(dispose.IsCompleted);
        release.Set();
        await Task.WhenAll(callback, dispose).WaitAsync(TimeSpan.FromSeconds(2));

        scheduler.Request();
        await Task.Delay(25);
        Assert.Equal(1, callbackCount);
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(2);
        while (!predicate())
        {
            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException("The scheduled callback was not dispatched.");
            }

            await Task.Delay(5);
        }
    }
}
