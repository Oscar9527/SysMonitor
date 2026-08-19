using SysMonitor.Services;

namespace SysMonitor.Tests;

public sealed class UiRefreshSchedulerTests
{
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
}
