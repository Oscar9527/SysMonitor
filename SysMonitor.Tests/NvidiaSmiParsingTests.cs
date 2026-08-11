using SysMonitor.Services;

namespace SysMonitor.Tests;

public sealed class NvidiaSmiParsingTests
{
    [Fact]
    public void ParsesQuotedCommaAndEachMetricIndependently()
    {
        const string line =
            "2026/08/10 12:34:56.000, 2, GPU-ABC, 00000000:01:00.0, \"RTX, Special\", N/A, 67, 12.5, N/A, 1875, 9501";

        Assert.True(NvidiaSmiCsv.TryParseRow(line, out NvidiaSmiRow row));
        Assert.Equal(2, row.Index);
        Assert.Equal("RTX, Special", row.Name);
        Assert.Equal("GPU-ABC", row.Uuid);
        Assert.Null(row.UsagePercent);
        Assert.Equal(67, row.TemperatureCelsius);
        Assert.Equal(13107200, row.MemoryUsedBytes);
        Assert.Null(row.MemoryTotalBytes);
        Assert.Equal(1875, row.CoreClockMhz);
        Assert.Equal(9501, row.MemoryClockMhz);
    }

    [Fact]
    public void RejectsMalformedRequiredFieldsButAllowsMissingOptionalIdentity()
    {
        Assert.False(NvidiaSmiCsv.TryParseRow(
            "bad timestamp, 0, N/A, N/A, GPU, 1, 2, 3, 4, 5, 6",
            out _));
        Assert.True(NvidiaSmiCsv.TryParseRow(
            "2026/08/10 12:34:56, 0, N/A, N/A, GPU, 1, 2, 3, 4, N/A, N/A",
            out NvidiaSmiRow row));
        Assert.Null(row.Uuid);
        Assert.Null(row.PciBusId);
    }

    [Fact]
    public void TimestampBoundaryPublishesPriorNonemptyPartialCycle()
    {
        var accumulator = new NvidiaSmiCycleAccumulator();
        Assert.True(accumulator.PushLine(Row("12:00:00", 0, 10), 100, out var first));
        Assert.Null(first);
        Assert.True(accumulator.PushLine(Row("12:00:00", 2, 20), 101, out var second));
        Assert.Null(second);

        Assert.True(accumulator.PushLine(Row("12:00:01", 0, 30), 200, out var completed));
        Assert.NotNull(completed);
        Assert.Equal(2, completed!.Samples.Count);
        Assert.Equal(new[] { 0, 2 }, completed.Samples.Select(sample => sample.Index));
        Assert.All(completed.Samples, sample => Assert.Equal(100, sample.MonotonicTimestamp));
    }

    [Fact]
    public void CorruptRowsDoNotPublishOrContaminateCycle()
    {
        var accumulator = new NvidiaSmiCycleAccumulator();
        Assert.True(accumulator.PushLine(Row("12:00:00", 0, 10), 100, out _));
        Assert.False(accumulator.PushLine("corrupt", 150, out var corruptResult));
        Assert.Null(corruptResult);
        Assert.True(accumulator.PushLine(Row("12:00:01", 1, 20), 200, out var completed));
        Assert.Single(completed!.Samples);
        Assert.Equal(0, completed.Samples[0].Index);
    }

    [Fact]
    public void DuplicateIndexWithinTimestampIsCorrupt()
    {
        var accumulator = new NvidiaSmiCycleAccumulator();
        Assert.True(accumulator.PushLine(Row("12:00:00", 0, 10), 100, out _));
        Assert.False(accumulator.PushLine(Row("12:00:00", 0, 20), 101, out _));
    }

    private static string Row(string time, int index, int usage) =>
        $"2026/08/10 {time}, {index}, GPU-{index}, 00000000:0{index}:00.0, GPU {index}, {usage}, 60, 100, 1000, 1800, 9000";
}
