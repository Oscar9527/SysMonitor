namespace SysMonitor.Models;

internal sealed class BandClickDebouncer
{
    private readonly ulong _minimumIntervalTicks;
    private bool _hasAcceptedTimestamp;
    private long _lastAcceptedTimestamp;

    public BandClickDebouncer(TimeSpan minimumInterval, long timestampFrequency)
    {
        if (minimumInterval < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(minimumInterval));
        }

        if (timestampFrequency <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(timestampFrequency));
        }

        double intervalTicks = minimumInterval.TotalSeconds * timestampFrequency;
        if (!double.IsFinite(intervalTicks) || intervalTicks > ulong.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(minimumInterval));
        }

        _minimumIntervalTicks = (ulong)Math.Ceiling(intervalTicks);
    }

    public bool TryAccept(long timestamp)
    {
        if (!_hasAcceptedTimestamp || timestamp < _lastAcceptedTimestamp)
        {
            _hasAcceptedTimestamp = true;
            _lastAcceptedTimestamp = timestamp;
            return true;
        }

        ulong elapsedTicks = unchecked((ulong)timestamp - (ulong)_lastAcceptedTimestamp);
        if (elapsedTicks < _minimumIntervalTicks)
        {
            return false;
        }

        _lastAcceptedTimestamp = timestamp;
        return true;
    }
}
