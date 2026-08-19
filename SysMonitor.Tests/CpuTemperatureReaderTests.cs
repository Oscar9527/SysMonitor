using System.Diagnostics;
using LibreHardwareMonitor.Hardware;
using SysMonitor.Services;

namespace SysMonitor.Tests;

public sealed class CpuTemperatureReaderTests
{
    [Fact]
    public void Start_WhenHardwareOpenBlocks_ReturnsImmediatelyAndDoesNotBlockRead()
    {
        using var openEntered = new ManualResetEventSlim();
        using var releaseOpen = new ManualResetEventSlim();
        using var reader = new CpuTemperatureReader(
            _ => new Computer(),
            _ =>
            {
                openEntered.Set();
                releaseOpen.Wait();
            });

        var stopwatch = Stopwatch.StartNew();
        reader.Start();
        stopwatch.Stop();

        Assert.True(stopwatch.Elapsed < TimeSpan.FromMilliseconds(500));
        Assert.True(openEntered.Wait(TimeSpan.FromSeconds(2)));
        Assert.True(reader.OpenInProgress);

        stopwatch.Restart();
        for (int index = 0; index < 20; index++)
        {
            Assert.Null(reader.Read());
        }

        stopwatch.Stop();
        Assert.True(stopwatch.Elapsed < TimeSpan.FromMilliseconds(500));
        Assert.False(reader.MotherboardFallbackAttempted);

        releaseOpen.Set();
        Assert.True(SpinWait.SpinUntil(() => reader.HasOpenComputer, TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public void Dispose_WhileHardwareOpenBlocks_DiscardsLateOpenResult()
    {
        using var openEntered = new ManualResetEventSlim();
        using var releaseOpen = new ManualResetEventSlim();
        var reader = new CpuTemperatureReader(
            _ => new Computer(),
            _ =>
            {
                openEntered.Set();
                releaseOpen.Wait();
            });

        reader.Start();
        Assert.True(openEntered.Wait(TimeSpan.FromSeconds(2)));

        var stopwatch = Stopwatch.StartNew();
        reader.Dispose();
        stopwatch.Stop();
        Assert.True(stopwatch.Elapsed < TimeSpan.FromMilliseconds(500));

        releaseOpen.Set();
        Assert.True(SpinWait.SpinUntil(() => !reader.OpenInProgress, TimeSpan.FromSeconds(2)));
        Assert.False(reader.HasOpenComputer);
    }

    [Fact]
    public void StopThenStart_DoesNotOverlapHardwareSessionsOrAcceptStaleResult()
    {
        using var firstOpenEntered = new ManualResetEventSlim();
        using var releaseFirstOpen = new ManualResetEventSlim();
        int openCount = 0;
        int activeOpens = 0;
        int maximumConcurrentOpens = 0;
        using var reader = new CpuTemperatureReader(
            _ => new Computer(),
            _ =>
            {
                int currentOpen = Interlocked.Increment(ref openCount);
                int concurrent = Interlocked.Increment(ref activeOpens);
                InterlockedExtensions.Max(ref maximumConcurrentOpens, concurrent);
                try
                {
                    if (currentOpen == 1)
                    {
                        firstOpenEntered.Set();
                        releaseFirstOpen.Wait();
                    }
                }
                finally
                {
                    Interlocked.Decrement(ref activeOpens);
                }
            });

        reader.Start();
        Assert.True(firstOpenEntered.Wait(TimeSpan.FromSeconds(2)));
        reader.Stop();
        reader.Start();

        Thread.Sleep(100);
        Assert.Equal(1, Volatile.Read(ref openCount));
        Assert.False(reader.HasOpenComputer);

        releaseFirstOpen.Set();
        Assert.True(SpinWait.SpinUntil(() => reader.HasOpenComputer, TimeSpan.FromSeconds(2)));
        Assert.Equal(2, Volatile.Read(ref openCount));
        Assert.Equal(1, Volatile.Read(ref maximumConcurrentOpens));
    }

    private static class InterlockedExtensions
    {
        internal static void Max(ref int target, int value)
        {
            int current = Volatile.Read(ref target);
            while (current < value)
            {
                int observed = Interlocked.CompareExchange(ref target, value, current);
                if (observed == current)
                {
                    return;
                }

                current = observed;
            }
        }
    }
}
