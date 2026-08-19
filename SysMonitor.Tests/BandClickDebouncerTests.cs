using SysMonitor.Models;

namespace SysMonitor.Tests;

public sealed class BandClickDebouncerTests
{
    private static BandClickDebouncer CreateDebouncer() =>
        new(TimeSpan.FromMilliseconds(350), timestampFrequency: 1000);

    [Fact]
    public void FirstTimestampIsAccepted()
    {
        Assert.True(CreateDebouncer().TryAccept(42));
    }

    [Fact]
    public void ExactBoundaryIsAccepted()
    {
        BandClickDebouncer debouncer = CreateDebouncer();

        Assert.True(debouncer.TryAccept(1000));
        Assert.False(debouncer.TryAccept(1349));
        Assert.True(debouncer.TryAccept(1350));
    }

    [Fact]
    public void TimestampRollbackStartsANewInterval()
    {
        BandClickDebouncer debouncer = CreateDebouncer();

        Assert.True(debouncer.TryAccept(1000));
        Assert.True(debouncer.TryAccept(900));
        Assert.False(debouncer.TryAccept(901));
        Assert.True(debouncer.TryAccept(1250));
    }

    [Fact]
    public void RepeatedTimestampsDoNotExtendSuppressionFromLastAcceptedClick()
    {
        BandClickDebouncer debouncer = CreateDebouncer();

        Assert.True(debouncer.TryAccept(0));
        Assert.False(debouncer.TryAccept(100));
        Assert.False(debouncer.TryAccept(100));
        Assert.False(debouncer.TryAccept(349));
        Assert.True(debouncer.TryAccept(350));
        Assert.False(debouncer.TryAccept(350));
    }
}
