using SysMonitor.Services;

namespace SysMonitor.Tests;

public sealed class MemoryFrequencyReaderTests
{
    [Fact]
    public void ConfiguredClockSpeedTakesPrecedenceOverFallbackSpeed()
    {
        double? result = MemoryFrequencyReader.SelectConfiguredMhz(
            new uint?[] { 3200, 3200 },
            new uint?[] { 2666, 2666 });

        Assert.Equal(3200d, result);
    }

    [Fact]
    public void FallsBackToReportedSpeedWhenConfiguredClockSpeedIsMissing()
    {
        double? result = MemoryFrequencyReader.SelectConfiguredMhz(
            new uint?[] { null, 0 },
            new uint?[] { 5600, 5600 });

        Assert.Equal(5600d, result);
    }

    [Fact]
    public void MissingOrImplausibleClockSpeedsRemainUnknown()
    {
        Assert.Null(MemoryFrequencyReader.SelectConfiguredMhz(
            new uint?[] { 0, uint.MaxValue },
            Array.Empty<uint?>()));
    }
}
