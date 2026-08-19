using System.Collections.Immutable;
using SysMonitor.Models;

namespace SysMonitor.Tests;

public sealed class MetricHistoryTests
{
    [Fact]
    public void CapacityWrapPreservesNewestPointsInChronologicalOrder()
    {
        var buffer = new MetricHistoryBuffer(1, TimeSpan.FromSeconds(60), 3);

        for (long sequence = 1; sequence <= 5; sequence++)
        {
            Assert.True(buffer.TryAdd(Point(sequence, sequence)));
        }

        Assert.Equal(new long[] { 3, 4, 5 }, buffer.Snapshot().Select(point => point.Sequence));
    }

    [Fact]
    public void DefaultBufferHasHardCapOfOneHundredTwentyPoints()
    {
        var buffer = new MetricHistoryBuffer();
        for (long sequence = 1; sequence <= MetricHistoryBuffer.DefaultCapacity + 1; sequence++)
        {
            Assert.True(buffer.TryAdd(Point(sequence, sequence)));
        }

        ImmutableArray<MetricHistoryPoint> snapshot = buffer.Snapshot();
        Assert.Equal(MetricHistoryBuffer.DefaultCapacity, snapshot.Length);
        Assert.Equal(2, snapshot[0].Sequence);
        Assert.Equal(121, snapshot[^1].Sequence);
    }

    [Fact]
    public void RealWindowRemovesOnlyPointsOlderThanExactBoundary()
    {
        var buffer = new MetricHistoryBuffer(10, TimeSpan.FromSeconds(6), 20);
        Assert.True(buffer.TryAdd(Point(1, 10)));
        Assert.True(buffer.TryAdd(Point(2, 69)));
        Assert.True(buffer.TryAdd(Point(3, 70)));

        Assert.Equal(new long[] { 1, 2, 3 }, buffer.Snapshot().Select(point => point.Sequence));

        Assert.True(buffer.TryAdd(Point(4, 71)));
        Assert.Equal(new long[] { 2, 3, 4 }, buffer.Snapshot().Select(point => point.Sequence));
    }

    [Fact]
    public void NewProducerStartsFreshEpochAndMayRestartSequenceAndTimestamp()
    {
        var buffer = new MetricHistoryBuffer(1, TimeSpan.FromSeconds(60), 10);
        Assert.True(buffer.TryAdd(Point(10, 100, producerId: 1)));
        Assert.True(buffer.TryAdd(Point(11, 101, producerId: 1)));

        Assert.True(buffer.TryAdd(Point(1, 1, producerId: 2)));

        MetricHistoryPoint only = Assert.Single(buffer.Snapshot());
        Assert.Equal(2, only.ProducerId);
        Assert.Equal(1, only.Sequence);
    }

    [Fact]
    public void DuplicateAndOutOfOrderSamplesAreRejectedWithoutMutatingSnapshot()
    {
        var buffer = new MetricHistoryBuffer(1, TimeSpan.FromSeconds(60), 10);
        Assert.True(buffer.TryAdd(Point(5, 50)));
        ImmutableArray<MetricHistoryPoint> before = buffer.Snapshot();

        Assert.False(buffer.TryAdd(Point(5, 51)));
        Assert.False(buffer.TryAdd(Point(6, 50)));
        Assert.False(buffer.TryAdd(Point(4, 49)));

        Assert.Equal(before.AsEnumerable(), buffer.Snapshot().AsEnumerable());
    }

    [Fact]
    public void SequenceAndMonotonicTimestampAreTheOnlyOrderingInputs()
    {
        var buffer = new MetricHistoryBuffer(1, TimeSpan.FromSeconds(60), 10);

        Assert.True(buffer.TryAdd(Point(long.MinValue, long.MinValue)));
        Assert.True(buffer.TryAdd(Point(long.MinValue + 1, long.MinValue + 1)));

        Assert.Equal(2, buffer.Snapshot().Length);
    }

    [Fact]
    public void PercentValuesClampAndNonFiniteValuesBecomeNullWhileZeroRemainsValid()
    {
        var buffer = new MetricHistoryBuffer(1, TimeSpan.FromSeconds(60), 10);
        Assert.True(buffer.TryAdd(new MetricHistoryPoint(1, 1, 1, -4, 140)));
        Assert.True(buffer.TryAdd(new MetricHistoryPoint(1, 2, 2, 0, null)));
        Assert.True(buffer.TryAdd(new MetricHistoryPoint(1, 3, 3, double.NaN, double.PositiveInfinity)));
        Assert.True(buffer.TryAdd(new MetricHistoryPoint(1, 4, 4, double.NegativeInfinity, 100)));

        ImmutableArray<MetricHistoryPoint> points = buffer.Snapshot();
        Assert.Equal(0, points[0].CpuUsagePercent);
        Assert.Equal(100, points[0].GpuUsagePercent);
        Assert.Equal(0, points[1].CpuUsagePercent);
        Assert.Null(points[1].GpuUsagePercent);
        Assert.Null(points[2].CpuUsagePercent);
        Assert.Null(points[2].GpuUsagePercent);
        Assert.Null(points[3].CpuUsagePercent);
        Assert.Equal(100, points[3].GpuUsagePercent);
    }

    [Fact]
    public void SnapshotIsIndependentFromLaterRingMutations()
    {
        var buffer = new MetricHistoryBuffer(1, TimeSpan.FromSeconds(60), 2);
        Assert.True(buffer.TryAdd(Point(1, 1)));
        Assert.True(buffer.TryAdd(Point(2, 2)));
        ImmutableArray<MetricHistoryPoint> first = buffer.Snapshot();
        ImmutableArray<MetricHistoryPoint> sameContent = buffer.Snapshot();

        Assert.True(buffer.TryAdd(Point(3, 3)));
        ImmutableArray<MetricHistoryPoint> second = buffer.Snapshot();

        Assert.Equal(new long[] { 1, 2 }, first.Select(point => point.Sequence));
        Assert.Equal(new long[] { 2, 3 }, second.Select(point => point.Sequence));
        Assert.False(first.Equals(sameContent));
    }

    [Fact]
    public void ConstructorRejectsInvalidClockWindowAndCapacity()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new MetricHistoryBuffer(0, TimeSpan.FromSeconds(1), 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new MetricHistoryBuffer(1, TimeSpan.Zero, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new MetricHistoryBuffer(1, TimeSpan.FromSeconds(1), 0));
    }

    private static MetricHistoryPoint Point(long sequence, long timestamp, long producerId = 1) =>
        new(producerId, sequence, timestamp, 50, 25);
}
