using System.Collections.Immutable;
using System.Diagnostics;

namespace SysMonitor.Models;

public readonly record struct MetricHistoryPoint(
    long ProducerId,
    long Sequence,
    long MonotonicTimestamp,
    double? CpuUsagePercent,
    double? GpuUsagePercent);

public sealed class MetricHistoryBuffer
{
    public const int DefaultCapacity = 120;

    private static readonly TimeSpan DefaultWindow = TimeSpan.FromSeconds(60);

    private readonly object _sync = new();
    private readonly MetricHistoryPoint[] _points;
    private readonly long _windowTicks;
    private int _start;
    private int _count;
    private long _producerId;
    private long _lastSequence;
    private long _lastTimestamp;
    private bool _hasProducer;

    public MetricHistoryBuffer()
        : this(Stopwatch.Frequency, DefaultWindow, DefaultCapacity)
    {
    }

    public MetricHistoryBuffer(long frequency, TimeSpan window, int capacity)
    {
        if (frequency <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(frequency));
        }

        if (window <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(window));
        }

        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        double windowTicks = window.TotalSeconds * frequency;
        if (!double.IsFinite(windowTicks) || windowTicks > long.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(window));
        }

        _windowTicks = Math.Max(1, checked((long)Math.Ceiling(windowTicks)));
        _points = new MetricHistoryPoint[capacity];
    }

    public bool TryAdd(MetricHistoryPoint point)
    {
        lock (_sync)
        {
            if (!_hasProducer || point.ProducerId != _producerId)
            {
                ClearCore();
                _producerId = point.ProducerId;
                _hasProducer = true;
            }
            else if (point.Sequence <= _lastSequence || point.MonotonicTimestamp <= _lastTimestamp)
            {
                return false;
            }

            point = point with
            {
                CpuUsagePercent = NormalizePercent(point.CpuUsagePercent),
                GpuUsagePercent = NormalizePercent(point.GpuUsagePercent),
            };

            AppendCore(point);
            _lastSequence = point.Sequence;
            _lastTimestamp = point.MonotonicTimestamp;
            RemoveExpiredCore(point.MonotonicTimestamp);
            return true;
        }
    }

    public ImmutableArray<MetricHistoryPoint> Snapshot()
    {
        lock (_sync)
        {
            if (_count == 0)
            {
                return ImmutableArray<MetricHistoryPoint>.Empty;
            }

            var builder = ImmutableArray.CreateBuilder<MetricHistoryPoint>(_count);
            for (int index = 0; index < _count; index++)
            {
                builder.Add(_points[PhysicalIndex(index)]);
            }

            return builder.MoveToImmutable();
        }
    }

    private static double? NormalizePercent(double? value)
    {
        if (!value.HasValue || !double.IsFinite(value.Value))
        {
            return null;
        }

        return Math.Clamp(value.Value, 0, 100);
    }

    private void AppendCore(MetricHistoryPoint point)
    {
        if (_count == _points.Length)
        {
            _points[_start] = point;
            _start = (_start + 1) % _points.Length;
            return;
        }

        _points[PhysicalIndex(_count)] = point;
        _count++;
    }

    private void RemoveExpiredCore(long latestTimestamp)
    {
        long cutoff = latestTimestamp < long.MinValue + _windowTicks
            ? long.MinValue
            : latestTimestamp - _windowTicks;

        while (_count > 0 && _points[_start].MonotonicTimestamp < cutoff)
        {
            _points[_start] = default;
            _start = (_start + 1) % _points.Length;
            _count--;
        }

        if (_count == 0)
        {
            _start = 0;
        }
    }

    private int PhysicalIndex(int logicalIndex) => (_start + logicalIndex) % _points.Length;

    private void ClearCore()
    {
        if (_count > 0)
        {
            Array.Clear(_points);
        }

        _start = 0;
        _count = 0;
        _lastSequence = 0;
        _lastTimestamp = 0;
    }
}
