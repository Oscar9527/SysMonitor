using System.Collections.Immutable;
using System.Windows;
using SysMonitor.Models;
using Point = System.Windows.Point;

namespace SysMonitor.UI;

internal readonly record struct HistorySparklineGeometry(
    ImmutableArray<ImmutableArray<Point>> Segments,
    Point? LatestValidPoint)
{
    internal static HistorySparklineGeometry Build(
        ImmutableArray<MetricHistoryPoint> series,
        MetricHistorySeries metric,
        double width,
        double height,
        long frequency,
        TimeSpan window,
        TimeSpan gapThreshold)
    {
        if (series.IsDefaultOrEmpty ||
            !double.IsFinite(width) || width <= 0 ||
            !double.IsFinite(height) || height <= 0 ||
            frequency <= 0 || window <= TimeSpan.Zero || gapThreshold < TimeSpan.Zero)
        {
            return Empty;
        }

        double windowTicks = window.TotalSeconds * frequency;
        double gapTicks = gapThreshold.TotalSeconds * frequency;
        if (!double.IsFinite(windowTicks) || windowTicks <= 0 ||
            !double.IsFinite(gapTicks) || gapTicks < 0)
        {
            return Empty;
        }

        long latestTimestamp = series[^1].MonotonicTimestamp;
        double domainStart = latestTimestamp - windowTicks;
        var segments = ImmutableArray.CreateBuilder<ImmutableArray<Point>>();
        var current = ImmutableArray.CreateBuilder<Point>();
        long previousTimestamp = 0;
        bool hasPrevious = false;
        Point? latestValidPoint = null;

        foreach (MetricHistoryPoint sample in series)
        {
            double? nullableValue = metric == MetricHistorySeries.Gpu
                ? sample.GpuUsagePercent
                : sample.CpuUsagePercent;

            if (!nullableValue.HasValue || !double.IsFinite(nullableValue.Value) ||
                sample.MonotonicTimestamp < domainStart ||
                sample.MonotonicTimestamp > latestTimestamp)
            {
                FinishSegment(current, segments);
                hasPrevious = false;
                continue;
            }

            if (hasPrevious && sample.MonotonicTimestamp - (double)previousTimestamp > gapTicks)
            {
                FinishSegment(current, segments);
            }

            double value = Math.Clamp(nullableValue.Value, 0, 100);
            double x = (sample.MonotonicTimestamp - domainStart) / windowTicks * width;
            double y = height - (value / 100 * height);
            var point = new Point(x, y);
            current.Add(point);
            latestValidPoint = point;
            previousTimestamp = sample.MonotonicTimestamp;
            hasPrevious = true;
        }

        FinishSegment(current, segments);
        return new HistorySparklineGeometry(segments.ToImmutable(), latestValidPoint);
    }

    internal static HistorySparklineGeometry Empty { get; } = new(
        ImmutableArray<ImmutableArray<Point>>.Empty,
        null);

    private static void FinishSegment(
        ImmutableArray<Point>.Builder current,
        ImmutableArray<ImmutableArray<Point>>.Builder segments)
    {
        if (current.Count == 0)
        {
            return;
        }

        segments.Add(current.ToImmutable());
        current.Clear();
    }
}
