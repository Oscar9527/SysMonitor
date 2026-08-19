using SysMonitor.Services;

namespace SysMonitor.Tests;

public sealed class FrameRateAggregatorTests
{
    [Fact]
    public void ComputesOneSecondPerSwapchainIntervals()
    {
        var aggregator = new FrameRateAggregator();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        Assert.True(aggregator.Add(Frame(1, 1.000, 10), now));
        Assert.True(aggregator.Add(Frame(1, 1.010, 10), now.AddMilliseconds(10)));
        Assert.True(aggregator.Add(Frame(1, 1.020, 20), now.AddMilliseconds(20)));

        Assert.Equal(75d, aggregator.Read(now.AddMilliseconds(20))!.Value, 6);
    }

    [Fact]
    public void RejectsNonMonotonicTimePerSwapchain()
    {
        var aggregator = new FrameRateAggregator();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        Assert.True(aggregator.Add(Frame(1, 2, 16), now));
        Assert.False(aggregator.Add(Frame(1, 2, 16), now.AddMilliseconds(1)));
        Assert.True(aggregator.Add(Frame(2, 1, 16), now.AddMilliseconds(1)));
    }

    [Fact]
    public void ChallengerNeedsTwoUpdatedWindowsAtLeastTwentyFivePercentFaster()
    {
        var aggregator = new FrameRateAggregator();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        AddPair(aggregator, 1, 1, 20, now);
        Assert.Equal(50d, aggregator.Read(now.AddMilliseconds(20))!.Value, 6);

        AddPair(aggregator, 2, 1, 10, now.AddMilliseconds(30));
        Assert.Equal(50d, aggregator.Read(now.AddMilliseconds(50))!.Value, 6);
        Assert.Equal(50d, aggregator.Read(now.AddMilliseconds(60))!.Value, 6);

        Assert.True(aggregator.Add(Frame(2, 1.02, 10), now.AddMilliseconds(70)));
        Assert.Equal(100d, aggregator.Read(now.AddMilliseconds(70))!.Value, 6);
    }

    [Fact]
    public void StaleCurrentSwitchesAndGlobalReceiveStaleClearsFps()
    {
        var aggregator = new FrameRateAggregator();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        AddPair(aggregator, 1, 1, 10, now);
        Assert.Equal(100d, aggregator.Read(now.AddMilliseconds(20))!.Value, 6);

        AddPair(aggregator, 2, 2, 20, now.AddSeconds(1.6));
        Assert.Equal(50d, aggregator.Read(now.AddSeconds(1.62))!.Value, 6);
        Assert.Null(aggregator.Read(now.AddSeconds(3.7)));
    }

    [Fact]
    public void CurrentChainFpsIsRetainedUntilGlobalTwoSecondStaleBoundary()
    {
        var aggregator = new FrameRateAggregator();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        AddPair(aggregator, 1, 1, 16, now);

        Assert.NotNull(aggregator.Read(now.AddSeconds(1.9)));
        Assert.Null(aggregator.Read(now.AddSeconds(2.1)));
    }

    private static void AddPair(
        FrameRateAggregator aggregator,
        ulong chain,
        double start,
        double milliseconds,
        DateTimeOffset receivedAt)
    {
        Assert.True(aggregator.Add(Frame(chain, start, milliseconds), receivedAt));
        Assert.True(aggregator.Add(
            Frame(chain, start + milliseconds / 1000d, milliseconds),
            receivedAt.AddMilliseconds(milliseconds)));
    }

    private static PresentMonFrame Frame(
        ulong swapChain,
        double timeInSeconds,
        double milliseconds) =>
        new(42, swapChain, timeInSeconds, milliseconds, "Game.exe");
}
